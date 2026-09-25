using System.Globalization;
using PairSync.Application.Presence;
using PairSync.Desktop.Resources;

namespace PairSync.Desktop.Tray;

/// <summary>Head of the tray menu: "PairSync · Connected" / "LAN · laptop-win11 · 1 transfer running".</summary>
public sealed record TrayStatus(string Headline, string Detail)
{
    public static TrayStatus Describe(IReadOnlyList<NearbyDevice> devices, int runningTransfers)
    {
        var online = devices.Where(d => d.State == PresenceState.Online).Select(d => d.Name).ToList();
        var transfers = runningTransfers switch
        {
            0 => Strings.Tray_TransfersNone,
            1 => Strings.Tray_TransfersOne,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Tray_TransfersMany, runningTransfers),
        };
        if (online.Count == 0)
            return new TrayStatus(Strings.Tray_NotConnected, transfers);

        var names = online.Count <= 2 ? string.Join(", ", online) : $"{online[0]}, {online[1]} +{online.Count - 2}";
        return new TrayStatus(Strings.Tray_Connected, $"LAN · {names} · {transfers}");
    }
}
