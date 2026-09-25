using Avalonia;
using PairSync.Application;
using PairSync.Storage;

namespace PairSync.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The core starts before the UI thread exists, so blocking here cannot deadlock.
        var core = PairSyncCore.StartAsync(DataDirectory.Default(), CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            return BuildAvaloniaApp(core).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            core.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Also used by the Avalonia previewer, which runs without a core.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(PairSyncCore? core) =>
        AppBuilder.Configure(() => new App { Core = core })
            .UsePlatformDetect()
            .LogToTrace();
}
