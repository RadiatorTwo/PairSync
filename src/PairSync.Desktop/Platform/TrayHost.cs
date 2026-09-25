using Tmds.DBus.Protocol;

namespace PairSync.Desktop.Platform;

/// <summary>Whether the desktop can show a tray icon at all.</summary>
internal static class TrayHost
{
    /// <summary>Tray icons on Linux are StatusNotifierItems; without this watcher nobody shows them (GNOME without AppIndicator).</summary>
    public const string StatusNotifierWatcher = "org.kde.StatusNotifierWatcher";

    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return true;
        if (DBusAddress.Session is not { Length: > 0 } address)
            return false;
        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var names = await connection.ListServicesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return names.Contains(StatusNotifierWatcher);
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}
