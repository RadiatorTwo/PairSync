using System.Globalization;
using PairSync.Application.Presence;
using PairSync.Desktop.Resources;

namespace PairSync.Desktop.Tray;

/// <summary>Head of the tray menu: "PairSync · Connected" / "LAN · laptop-win11 · 1 transfer running".</summary>
public sealed record TrayStatus(string Headline, string Detail)
{
    /// <param name="internetOnly">Devices connected only over an internet link.</param>
    public static TrayStatus Describe(IReadOnlyList<NearbyDevice> devices, int runningTransfers, IReadOnlyList<string>? internetOnly = null)
    {
        var online = devices.Where(d => d.State == PresenceState.Online).Select(d => d.Name).ToList();
        internetOnly ??= [];
        var transfers = runningTransfers switch
        {
            0 => Strings.Tray_TransfersNone,
            1 => Strings.Tray_TransfersOne,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Tray_TransfersMany, runningTransfers),
        };
        if (online.Count == 0 && internetOnly.Count == 0)
            return new TrayStatus(Strings.Tray_NotConnected, transfers);

        static string Names(IReadOnlyList<string> names) =>
            names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} +{names.Count - 2}";
        var route = online.Count == 0 ? $"Internet · {Names(internetOnly)}"
            : internetOnly.Count == 0 ? $"LAN · {Names(online)}"
            : $"LAN · {Names(online)} · Internet · {Names(internetOnly)}";
        return new TrayStatus(Strings.Tray_Connected, $"{route} · {transfers}");
    }
}
