using System.Globalization;
using Avalonia.Media.Imaging;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Protocol;
using PairSync.Transport;
using QRCoder;

namespace PairSync.Desktop.ViewModels;

/// <summary>What a pasted or loaded text is, by its prefix.</summary>
public enum CodeKind
{
    None,

    /// <summary><c>PSI1</c> or <c>PSI2</c>: pairing invitation.</summary>
    Invitation,

    /// <summary><c>PSC1</c>: connection code from a paired device.</summary>
    ConnectionCode,

    /// <summary><c>PSR1</c>: answer to a connection code or an internet invitation.</summary>
    Answer,
}

/// <summary>Texts, QR codes and files of invitations, connection codes and answers.</summary>
internal static class Codes
{
    public static CodeKind Classify(string? text) =>
        string.IsNullOrWhiteSpace(text) ? CodeKind.None
        : InvitationCodec.LooksLikeInvitation(text) ? CodeKind.Invitation
        : ConnectCodec.LooksLikeOffer(text) ? CodeKind.ConnectionCode
        : ConnectCodec.LooksLikeAnswer(text) ? CodeKind.Answer
        : CodeKind.None;

    public static Bitmap QrCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // Dark modules in Text (#1D1F20) on the page background, 1 px per module; the view scales without smoothing.
        var png = new PngByteQRCode(data).GetGraphic(1, [0x1D, 0x1F, 0x20], [0xF2, 0xF2, 0xF3]);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    /// <summary>"08:41".</summary>
    public static string Countdown(TimeSpan left) =>
        left <= TimeSpan.Zero ? "00:00" : $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";

    /// <summary>Lets the user pick a code file and returns its text; null if canceled.</summary>
    /// <exception cref="IOException">Not readable or far too large for a code.</exception>
    public static async Task<string?> LoadFileAsync(IDesktopServices desktop, string extension)
    {
        if (await desktop.PickOpenFileAsync(extension) is not { } path)
            return null;
        try
        {
            if (new FileInfo(path).Length > CodeText.MaxTextLength)
                throw new IOException(Strings.Code_FileTooLarge);
            return await File.ReadAllTextAsync(path);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>Second line of a connected internet card: "Route: STUN (srflx) · manual code".</summary>
    public static string RouteLine(RouteInfo? route)
    {
        var type = route is null ? CandidateType.Unknown
            // The more telling side: a srflx or relay candidate on either end says how the path was found.
            : route.LocalType is CandidateType.Relay || route.RemoteType is CandidateType.Relay ? CandidateType.Relay
            : route.LocalType is CandidateType.ServerReflexive || route.RemoteType is CandidateType.ServerReflexive ? CandidateType.ServerReflexive
            : route.LocalType is CandidateType.PeerReflexive || route.RemoteType is CandidateType.PeerReflexive ? CandidateType.PeerReflexive
            : route.LocalType;
        var candidate = type switch
        {
            CandidateType.Host => Strings.Route_Host,
            CandidateType.ServerReflexive => Strings.Route_Srflx,
            CandidateType.PeerReflexive => Strings.Route_Prflx,
            CandidateType.Relay => Strings.Route_Relay,
            _ => Strings.Route_Unknown,
        };
        return string.Format(CultureInfo.CurrentCulture, Strings.Route_Line, candidate);
    }

    /// <summary>First line of a connected internet card: "Internet · direct · 41 ms".</summary>
    public static string InternetLine(TimeSpan? roundTrip) => roundTrip is { } rtt
        ? string.Format(CultureInfo.CurrentCulture, Strings.Card_RouteInternetRtt, Math.Max(1, (int)Math.Round(rtt.TotalMilliseconds)))
        : Strings.Card_RouteInternet;
}
