using System.Net;
using PairSync.Domain;
using PairSync.Application.Internet;
using PairSync.Protocol;
using PairSync.Stun;
using PairSync.Transport;

namespace PairSync.Application.Pairing;

/// <summary>An invitation from another device that passed all checks; the security code still has to be compared.</summary>
/// <param name="Offer">Internet invitation: the inviting device's WebRTC offer, to be answered with a connection answer code.</param>
public sealed record ReceivedInvitation(
    Guid DeviceId, string DeviceName, byte[] PublicKey, IReadOnlyList<IPAddress> Addresses, int Port, byte[] Nonce, DateTime ExpiresAtUtc,
    SessionDescription? Offer = null, NatHint RemoteNat = NatHint.Unknown)
{
    public DeviceFingerprint Fingerprint => DeviceFingerprint.Of(PublicKey);
}

/// <summary>An invitation this device issued: the text for clipboard, file and QR code.</summary>
public sealed record IssuedInvitation(string Text, DateTime ExpiresAtUtc, byte[] Nonce)
{
    public string SuggestedFileName(string deviceName) =>
        $"{string.Concat(deviceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}{InvitationCodec.FileExtension}";
}

/// <summary>Creates and checks signed invitations (plan §5, work package E).</summary>
public static class PairingInvitations
{
    public const int NonceSize = 16;

    /// <param name="offer">WebRTC offer for pairing over the internet; makes this a <c>PSI2</c> invitation.</param>
    public static string Create(
        DeviceIdentity identity, string deviceName, IReadOnlyList<IPAddress> addresses, int port, byte[] nonce, DateTime expiresAtUtc,
        SessionDescription? offer = null, NatHint nat = NatHint.Unknown)
    {
        if (offer is { Type: not SessionDescriptionType.Offer })
            throw new ArgumentException("Expected an offer.", nameof(offer));
        var payload = InvitationCodec.SerializePayload(new PairingInvitation
        {
            DeviceId = identity.Id,
            DeviceName = deviceName,
            PublicKey = identity.PublicKey,
            ProtocolMajor = ProtocolVersion.Major,
            ProtocolMinor = ProtocolVersion.Minor,
            Addresses = [.. addresses.Select(a => a.ToString())],
            Port = port,
            Nonce = nonce,
            ExpiresAtUtc = expiresAtUtc,
            Offer = offer is null ? null : CodeText.CompressSdp(offer.Sdp),
            NatHint = (byte)nat,
        });
        return InvitationCodec.Encode(payload, identity.Sign(InvitationCodec.SignedData(payload)), withOffer: offer is not null);
    }

    /// <summary>Checks signature, protocol version, expiry and content of a pasted or loaded invitation.</summary>
    /// <param name="clockSkew">Tolerated clock difference between the two devices; the inviting device enforces the real expiry.</param>
    /// <exception cref="InvalidInvitationException">With a message for the user.</exception>
    public static ReceivedInvitation Read(string text, DateTime nowUtc, TimeSpan clockSkew, Guid ownDeviceId)
    {
        SignedInvitation envelope;
        PairingInvitation invitation;
        try
        {
            (envelope, invitation) = InvitationCodec.Decode(text);
        }
        catch (FormatException e)
        {
            throw new InvalidInvitationException($"This is not a valid PairSync invitation. {e.Message}", e);
        }

        if (invitation.PublicKey is not { Length: > 0 } key ||
            !DeviceIdentity.Verify(key, InvitationCodec.SignedData(envelope.Payload!), envelope.Signature!))
            throw new InvalidInvitationException("The invitation was changed or damaged: its signature does not match.");
        if (invitation.ProtocolMajor != ProtocolVersion.Major)
            throw new InvalidInvitationException(
                $"The invitation comes from protocol version {invitation.ProtocolMajor}.{invitation.ProtocolMinor}, this device uses " +
                $"{ProtocolVersion.Current}. Update PairSync on the older device.");
        if (invitation.DeviceId == ownDeviceId)
            throw new InvalidInvitationException("This invitation comes from this device. Import it on the other device.");
        if (nowUtc > invitation.ExpiresAtUtc + clockSkew)
            throw new InvalidInvitationException("The invitation has expired. Create a new one on the other device.");
        if (invitation.Nonce is not { Length: NonceSize } nonce)
            throw new InvalidInvitationException("The invitation is incomplete.");

        var addresses = (invitation.Addresses ?? [])
            .Select(a => IPAddress.TryParse(a, out var address) ? address : null)
            .OfType<IPAddress>()
            .ToArray();
        SessionDescription? offer = null;
        if (invitation.Offer is not null)
        {
            try
            {
                offer = new SessionDescription(SessionDescriptionType.Offer, ConnectCodes.ReadSdp(invitation.Offer, "invitation"));
            }
            catch (InvalidConnectCodeException e)
            {
                throw new InvalidInvitationException(e.Message, e);
            }
        }
        // An internet invitation may come without LAN addresses (the inviting device is elsewhere).
        if (offer is null && (addresses.Length == 0 || invitation.Port is <= 0 or > 65535))
            throw new InvalidInvitationException("The invitation contains no address to connect to.");

        return new ReceivedInvitation(invitation.DeviceId, DeviceNames.Clean(invitation.DeviceName, invitation.DeviceId), key,
            addresses, invitation.Port is > 0 and <= 65535 ? invitation.Port : 0, nonce, invitation.ExpiresAtUtc,
            offer, ConnectCodes.ToHint(invitation.NatHint));
    }
}

/// <summary>
/// Invitations this device issued and nobody redeemed yet. Each nonce is accepted once, before it expires; a failed
/// or canceled pairing does not give it back.
/// </summary>
public sealed class InvitationBook(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTime> _open = [];

    public void Add(byte[] nonce, DateTime expiresAtUtc)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var expired in _open.Where(e => e.Value < now).Select(e => e.Key).ToList())
                _open.Remove(expired);
            _open[Convert.ToHexString(nonce)] = expiresAtUtc;
        }
    }

    public bool TryRedeem(byte[] nonce)
    {
        lock (_gate)
            return _open.Remove(Convert.ToHexString(nonce), out var expiresAtUtc) && time.GetUtcNow().UtcDateTime <= expiresAtUtc;
    }

    /// <summary>The user closed the invitation or created a new one.</summary>
    public void Revoke(byte[] nonce)
    {
        lock (_gate)
            _open.Remove(Convert.ToHexString(nonce));
    }
}

internal static class DeviceNames
{
    public const int MaxLength = 64;

    /// <summary>Names come from the other device: no control characters, bounded length, never empty.</summary>
    public static string Clean(string? name, Guid deviceId)
    {
        var cleaned = string.Concat((name ?? "").Where(c => !char.IsControl(c))).Trim();
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength].TrimEnd();
        return cleaned.Length > 0 ? cleaned : Presence.NearbyDevice.FoundName(deviceId);
    }
}
