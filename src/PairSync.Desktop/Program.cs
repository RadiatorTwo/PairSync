using Avalonia;
using PairSync.Application;
using PairSync.Desktop.Platform;
using PairSync.Storage;

namespace PairSync.Desktop;

internal static class Program
{
    /// <summary>Passed by the login entry: start in the tray without showing the window.</summary>
    public const string TrayArgument = "--tray";

    [STAThread]
    public static int Main(string[] args)
    {
        var dataDirectory = DataDirectory.Default();
        dataDirectory.EnsureCreated();

        // A second start shows the running app's window instead of opening the database and port again.
        using var instance = SingleInstance.TryAcquire(dataDirectory.Root);
        if (instance is null)
        {
            SingleInstance.ActivateRunningAsync(dataDirectory.Root, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            return 0;
        }

        // Before the UI thread exists, so blocking here cannot deadlock.
        var trayAvailable = TrayHost.IsAvailableAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token).GetAwaiter().GetResult();
        var core = PairSyncCore.StartAsync(dataDirectory, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            return BuildAvaloniaApp(new App
            {
                Core = core,
                TrayAvailable = trayAvailable,
                StartHidden = args.Contains(TrayArgument, StringComparer.Ordinal),
                Instance = instance,
            }).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            core.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Also used by the Avalonia previewer, which runs without a core.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(new App());

    private static AppBuilder BuildAvaloniaApp(App app) =>
        AppBuilder.Configure(() => app)
            .UsePlatformDetect()
            .LogToTrace();
}
