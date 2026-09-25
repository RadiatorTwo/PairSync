using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.Logging.Abstractions;
using PairSync.Desktop;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Storage;
using PairSync.Storage.Settings;

[assembly: AssemblyFixture(typeof(PairSync.UiTests.HeadlessFixture))]

namespace PairSync.UiTests;

/// <summary>One headless Avalonia app (with Skia, so frames can be captured) for all tests.</summary>
public sealed class HeadlessFixture : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    public HeadlessFixture()
    {
        // The assertions use the English texts of the mockups, whatever the machine's language.
        Strings.Culture = CultureInfo.GetCultureInfo("en");
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFixture));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs on the UI thread of the headless app.</summary>
    public Task RunAsync(Action test) => _session.Dispatch(test, TestContext.Current.CancellationToken);

    /// <summary>Runs an asynchronous test on the UI thread; awaits inside keep the dispatcher running.</summary>
    public Task RunAsync(Func<Task> test) => _session.Dispatch(async () =>
    {
        await test();
        return true;
    }, TestContext.Current.CancellationToken);

    /// <summary>Disposing the session can block on the dispatcher thread; the process ends right after anyway.</summary>
    public void Dispose() => Task.Run(_session.Dispose).Wait(TimeSpan.FromSeconds(5));
}

/// <summary>A data directory with settings, deleted afterwards.</summary>
public sealed class TempSettings : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-ui-");

    public TempSettings() => Store = Open();

    public SettingsStore Store { get; }

    public DataDirectory Data => new(_root.FullName);

    public SettingsStore Open() => new(Data, NullLogger<SettingsStore>.Instance);

    public void Dispose() => _root.Delete(recursive: true);
}

/// <summary>Keeps rendered frames when <c>PAIRSYNC_UI_SNAPSHOTS</c> names a folder, for a check against the mockups.</summary>
public static class Snapshots
{
    public static void Save(Avalonia.Controls.TopLevel window, string name)
    {
        var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        if (frame is not null && Environment.GetEnvironmentVariable("PAIRSYNC_UI_SNAPSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }
}

/// <summary>Waits on the UI thread, running posted work, until a condition holds.</summary>
public static class UiWait
{
    public static void Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The UI did not reach the expected state.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }
}

public sealed class FakeAutostart : IAutostart
{
    public bool IsSupported { get; init; } = true;

    public bool IsEnabled { get; private set; }

    public Exception? Failure { get; set; }

    public void Set(bool enabled)
    {
        if (Failure is not null)
            throw Failure;
        IsEnabled = enabled;
    }
}
