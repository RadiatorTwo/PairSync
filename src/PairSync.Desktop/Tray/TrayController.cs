using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Application.Presence;
using PairSync.Desktop.Resources;
using PairSync.Domain;

namespace PairSync.Desktop.Tray;

/// <summary>
/// Tray icon with the native menu: status, Open window, Pause all syncs, Quit PairSync — stops all syncing.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly Avalonia.Application _app;
    private readonly PairSyncCore _core;
    private readonly ILogger<TrayController> _logger;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _headline;
    private readonly NativeMenuItem _detail;
    private int _refreshQueued;

    public TrayController(Avalonia.Application app, PairSyncCore core, WindowIcon icon, Action open, Action quit)
    {
        _app = app;
        _core = core;
        _logger = core.Services.GetService(typeof(ILogger<TrayController>)) as ILogger<TrayController>
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TrayController>.Instance;

        // Disabled items act as the status header; native menus have no other way to show text.
        _headline = new NativeMenuItem(Strings.Tray_NotConnected) { IsEnabled = false };
        _detail = new NativeMenuItem(Strings.Tray_TransfersNone) { IsEnabled = false };
        var openItem = new NativeMenuItem(Strings.Tray_Open);
        openItem.Click += (_, _) => open();
        var pauseAll = new NativeMenuItem(Strings.Tray_PauseAll);
        pauseAll.Click += (_, _) => _ = PauseAllAsync();
        var quitItem = new NativeMenuItem(Strings.Tray_Quit);
        quitItem.Click += (_, _) => quit();

        _icon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = Strings.Tray_Tooltip,
            Menu = [_headline, _detail, new NativeMenuItemSeparator(), openItem, pauseAll, new NativeMenuItemSeparator(), quitItem],
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => open();
        TrayIcon.SetIcons(app, [_icon]);

        _core.Presence.Changed += QueueRefresh;
        _core.Transfers.Changed += QueueRefresh;
        _core.Internet.Changed += QueueRefresh;
        QueueRefresh();
    }

    /// <summary>Several events in a row cause one refresh.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Dispatcher.UIThread.Post(() => _ = RefreshAsync(), DispatcherPriority.Background);
    }

    private async Task RefreshAsync()
    {
        Volatile.Write(ref _refreshQueued, 0);
        try
        {
            var jobs = await _core.Transfers.GetJobsAsync(CancellationToken.None);
            var devices = _core.Presence.Devices;
            var internetOnly = devices.Where(d => d.State != PresenceState.Online && _core.Internet.LinkTo(d.Id) is not null).Select(d => d.Name).ToList();
            var status = TrayStatus.Describe(devices, jobs.Count(j => j.State == JobState.Running), internetOnly);
            _headline.Header = status.Headline;
            _detail.Header = status.Detail;
            _icon.ToolTipText = status.Headline;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogWarning(e, "Tray status could not be updated");
        }
    }

    private async Task PauseAllAsync()
    {
        try
        {
            await _core.Transfers.PauseAllAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogWarning(e, "Pausing all transfers from the tray failed");
        }
    }

    public void Dispose()
    {
        _core.Presence.Changed -= QueueRefresh;
        _core.Transfers.Changed -= QueueRefresh;
        _core.Internet.Changed -= QueueRefresh;
        TrayIcon.SetIcons(_app, null);
        _icon.Dispose();
    }
}
