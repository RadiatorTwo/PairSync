using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Application.Internet;
using PairSync.Application.Pairing;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Application.Sync;
using PairSync.Desktop;
using PairSync.Desktop.Platform;
using PairSync.Desktop.ViewModels;
using PairSync.Discovery.Lan;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;

namespace PairSync.UiTests;

/// <summary>Records what view models ask of the window; pickers return what the test prepared.</summary>
public sealed class FakeDesktop : IDesktopServices
{
    public List<string> FilesToPick { get; } = [];

    public string? FolderToPick { get; set; }

    public string? Copied { get; private set; }

    public int Reveals { get; private set; }

    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([.. FilesToPick]);

    public Task<string?> PickFolderAsync(string? startIn = null) => Task.FromResult(FolderToPick);

    public Task<string?> PickSaveFileAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);

    public Task<string?> PickOpenFileAsync(string extension) => Task.FromResult<string?>(null);

    public Task CopyTextAsync(string text)
    {
        Copied = text;
        return Task.CompletedTask;
    }

    public Task OpenFolderAsync(string path) => Task.CompletedTask;

    public void RevealWindow() => Reveals++;
}

/// <summary>An app core with its shell and window, as the desktop app builds them.</summary>
public sealed class TestApp(PairSyncCore core, ShellViewModel shell, MainWindow window, CoreEvents events, FakeDesktop desktop) : IAsyncDisposable
{
    public PairSyncCore Core { get; } = core;

    public ShellViewModel Shell { get; } = shell;

    public MainWindow Window { get; } = window;

    public FakeDesktop Desktop { get; } = desktop;

    public Guid Id => Core.Identity.Identity.Id;

    public T Page<T>() where T : PageViewModel => Shell.Page<T>();

    public async ValueTask DisposeAsync()
    {
        events.Dispose();
        Window.DataContext = null;
        Window.Close();
        Shell.Dispose();
        await Core.DisposeAsync();
    }
}

/// <summary>Two cores on loopback with separate data folders; mDNS is off, reachability is set by hand.</summary>
public sealed class TwoCores : IAsyncDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-screens-");
    private readonly List<TestApp> _apps = [];

    public string Root => _root.FullName;

    /// <param name="lanInvitations">False: invitations carry no LAN address, so the other device has to pair over the internet.</param>
    /// <param name="stunServers">STUN servers; none by default, so internet links use host candidates and nothing leaves the machine.</param>
    public async Task<TestApp> StartAsync(string name, bool lanInvitations = true, IReadOnlyList<string>? stunServers = null)
    {
        var data = new DataDirectory(Path.Combine(Root, name));
        var core = await PairSyncCore.StartAsync(data, CancellationToken.None, services =>
        {
            services.AddSingleton(new LanOptions { Port = 0 });
            services.AddSingleton(new PresenceOptions { Enabled = false });
            services.AddSingleton(new PairingOptions { InvitationAddresses = lanInvitations ? [IPAddress.Loopback] : [] });
            services.AddSingleton(new TransferOptions { DownloadsFolder = Path.Combine(data.Root, "downloads"), RetryInterval = TimeSpan.FromSeconds(1) });
            services.AddSingleton(new InternetOptions
            {
                DetectNat = false,
                GatheringTimeout = TimeSpan.FromSeconds(1),
                PingInterval = TimeSpan.FromMilliseconds(500),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
            services.AddSingleton(new SyncOptions { WatcherSettle = TimeSpan.FromMilliseconds(300), RetryInterval = TimeSpan.FromSeconds(1) });
        });
        core.Settings.Update(s => s with { DeviceName = name, StunServers = stunServers ?? [] });
        var desktop = new FakeDesktop();
        var shell = App.CreateShell(core, trayAvailable: true, new FakeAutostart(), desktop, TimeProvider.System, () => { });
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var app = new TestApp(core, shell, window, new CoreEvents(core, shell, desktop), desktop);
        _apps.Add(app);
        return app;
    }

    /// <summary>Stores each device in the other's list, skipping the pairing steps.</summary>
    public static async Task PairAsync(TestApp a, TestApp b)
    {
        await AddAsync(a.Core, b.Core);
        await AddAsync(b.Core, a.Core);
        await a.Core.Presence.ReloadPairedDevicesAsync(CancellationToken.None);
        await b.Core.Presence.ReloadPairedDevicesAsync(CancellationToken.None);
    }

    /// <summary>Lets <paramref name="owner"/> see <paramref name="other"/> online at its loopback port.</summary>
    public static void MakeReachable(TestApp owner, TestApp other) =>
        owner.Core.Presence.AddKnownEndpoint(new LanServiceInfo(other.Id, ProtocolVersion.Current, other.Core.Lan.Port, [IPAddress.Loopback],
            DateTime.UtcNow));

    private static async Task AddAsync(PairSyncCore owner, PairSyncCore other)
    {
        await using var db = await owner.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync();
        db.Devices.Add(new PairedDevice
        {
            Id = other.Identity.Identity.Id,
            Name = other.Settings.Current.EffectiveDeviceName,
            PublicKey = other.Identity.Identity.PublicKey,
            PairedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public string SourceFile(string name, int size)
    {
        var path = Path.Combine(Root, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(size));
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Waits without blocking the UI thread, so posted updates keep running.</summary>
public static class UiAsync
{
    public static async Task UntilAsync(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The UI did not reach the expected state.");
            await Task.Delay(20);
        }
    }
}
