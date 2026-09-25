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
