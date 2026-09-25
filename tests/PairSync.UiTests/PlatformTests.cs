using System.Globalization;
using Microsoft.Win32;
using PairSync.Application.Presence;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Desktop.Tray;

namespace PairSync.UiTests;

public sealed class PlatformTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-platform-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Xdg_entry_is_written_quoted_and_removed()
    {
        var autostart = new XdgAutostart(_root.FullName, ["/opt/Pair Sync/PairSync", "--tray"]);
        Assert.False(autostart.IsEnabled);

        autostart.Set(true);

        Assert.True(autostart.IsEnabled);
        Assert.Equal(Path.Combine(_root.FullName, "autostart", "pairsync.desktop"), autostart.EntryPath);
        var lines = File.ReadAllLines(autostart.EntryPath);
        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains("Exec=\"/opt/Pair Sync/PairSync\" \"--tray\"", lines);

        autostart.Set(false);
        Assert.False(autostart.IsEnabled);
        autostart.Set(false); // already gone
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b", "\"a\\\\\"b\"")]
    [InlineData("$HOME", "\"\\\\$HOME\"")]
    [InlineData(@"C:\x", "\"C:\\\\\\\\x\"")]
    [InlineData("100%", "\"100%%\"")]
    public void Xdg_exec_arguments_are_escaped(string argument, string expected) =>
        Assert.Equal(expected, XdgAutostart.QuoteExecArgument(argument));

    [Fact]
    public void Windows_run_key_value_is_set_and_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Registry only on Windows");
        if (!OperatingSystem.IsWindows())
            return;
        var keyPath = @"Software\PairSync.Tests\Run-" + Guid.NewGuid().ToString("N");
        try
        {
            var autostart = new WindowsRunKeyAutostart(keyPath, "PairSync", [@"C:\Program Files\PairSync\PairSync.exe", "--tray"]);
            Assert.False(autostart.IsEnabled);

            autostart.Set(true);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal("\"C:\\Program Files\\PairSync\\PairSync.exe\" \"--tray\"", key!.GetValue("PairSync"));
            Assert.True(autostart.IsEnabled);

            autostart.Set(false);
            Assert.False(autostart.IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\PairSync.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Second_instance_activates_the_first()
    {
        var first = SingleInstance.TryAcquire(_root.FullName);
        Assert.NotNull(first);
        using (first)
        {
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SetActivationHandler(() => activated.TrySetResult());

            // The mutex belongs to a thread; a real second start is another process, so try from another thread.
            SingleInstance? second = null;
            var thread = new Thread(() => second = SingleInstance.TryAcquire(_root.FullName));
            thread.Start();
            thread.Join();
            Assert.Null(second);

            Assert.True(await SingleInstance.ActivateRunningAsync(_root.FullName, TimeSpan.FromSeconds(5)));
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Activation_before_the_window_exists_is_delivered_later()
    {
        using var first = SingleInstance.TryAcquire(_root.FullName)!;

        Assert.True(await SingleInstance.ActivateRunningAsync(_root.FullName, TimeSpan.FromSeconds(5)));
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The request is read on the listener thread; wait until it has arrived.
        for (var i = 0; i < 50 && !delivered.Task.IsCompleted; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            first.SetActivationHandler(() => delivered.TrySetResult());
        }
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nobody_running_means_no_activation() =>
        Assert.False(await SingleInstance.ActivateRunningAsync(_root.FullName, TimeSpan.FromMilliseconds(300)));

    [Fact]
    public void Tray_status_names_online_devices_and_transfers()
    {
        var culture = CultureInfo.GetCultureInfo("en");
        Strings.Culture = culture;
        NearbyDevice Device(string name, PresenceState state) => new(Guid.NewGuid(), name, state, null, null, null);

        var none = TrayStatus.Describe([Device("nas-box", PresenceState.Offline)], 0);
        Assert.Equal("PairSync · No device online", none.Headline);
        Assert.Equal("No transfers", none.Detail);

        var one = TrayStatus.Describe([Device("laptop-win11", PresenceState.Online), Device("device-7F2A", PresenceState.Found)], 1);
        Assert.Equal("PairSync · Connected", one.Headline);
        Assert.Equal("LAN · laptop-win11 · 1 transfer running", one.Detail);

        var many = TrayStatus.Describe(
            [Device("a", PresenceState.Online), Device("b", PresenceState.Online), Device("c", PresenceState.Online)], 3);
        Assert.Equal("LAN · a, b +1 · 3 transfers running", many.Detail);

        var internet = TrayStatus.Describe([Device("office-pc", PresenceState.Offline)], 0, ["office-pc"]);
        Assert.Equal("PairSync · Connected", internet.Headline);
        Assert.Equal("Internet · office-pc · No transfers", internet.Detail);
    }

    [Fact]
    public void German_texts_exist_for_every_string()
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        var german = CultureInfo.GetCultureInfo("de");
        foreach (System.Collections.DictionaryEntry entry in english)
        {
            var key = (string)entry.Key;
            var translated = Strings.ResourceManager.GetString(key, german);
            Assert.False(string.IsNullOrEmpty(translated), key);
        }
        Assert.Equal("Geräte", Strings.ResourceManager.GetString(nameof(Strings.Nav_Devices), german));
        Assert.Equal("Fenster öffnen", Strings.ResourceManager.GetString(nameof(Strings.Tray_Open), german));
    }
}
