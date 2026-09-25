using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Application.Devices;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Domain;

namespace PairSync.Desktop.ViewModels;

/// <summary>A card under "Paired devices".</summary>
public sealed partial class PairedDeviceViewModel(PairedDevice device, DevicesViewModel owner) : ObservableObject
{
    public Guid Id => Device.Id;

    public PairedDevice Device { get; } = device;

    public string Name => Device.Name;

    public string PairedOn => string.Format(CultureInfo.CurrentCulture, Strings.Devices_PairedOn, Format.Date(Device.PairedAtUtc));

    public string Fingerprint => DeviceFingerprint.Of(Device.PublicKey).ToShortString();

    public bool IsBlocked => Device.Trust == DeviceTrust.Blocked;

    public bool CanSendToMe => Device.CanSendToMe;

    public string BlockLabel => IsBlocked ? Strings.Devices_Unblock : Strings.Devices_Block;

    [RelayCommand]
    private Task Permissions() => owner.ShowPermissionsAsync(this);

    [RelayCommand]
    private Task ToggleBlock() => owner.SetBlockedAsync(this, !IsBlocked);

    [RelayCommand]
    private Task Remove() => owner.RemoveAsync(this);
}

/// <summary>"Permissions for laptop-win11": what the device may do here.</summary>
public sealed partial class PermissionsDialogViewModel : DialogViewModel
{
    private readonly Func<bool, Task> _setCanSend;

    [ObservableProperty]
    private bool _canSendToMe;

    public PermissionsDialogViewModel(string deviceName, bool canSendToMe, Func<bool, Task> setCanSend)
    {
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Perm_Title, deviceName);
        _canSendToMe = canSendToMe;
        _setCanSend = setCanSend;
    }

    public string Title { get; }

    partial void OnCanSendToMeChanged(bool value) => _ = _setCanSend(value);

    [RelayCommand]
    private void Done() => Close();
}

/// <summary>Devices and pairing (screen 05).</summary>
public sealed partial class DevicesViewModel : PageViewModel, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly DialogHost _dialogs;
    private readonly ILogger<DevicesViewModel> _logger;
    private int _refreshQueued;
    private bool _disposed;

    [ObservableProperty]
    private string? _error;

    public DevicesViewModel(PairSyncCore core, DialogHost dialogs, IDesktopServices desktop, TimeProvider time) : base(AppPage.Devices)
    {
        _core = core;
        _dialogs = dialogs;
        _logger = core.Logger<DevicesViewModel>();
        Pairing = new PairingViewModel(core, desktop, time);
        Pairing.Finished += QueueRefresh;
        _core.Devices.Changed += QueueRefresh;
        _core.Presence.Changed += QueueRefresh;
        QueueRefresh();
    }

    public PairingViewModel Pairing { get; }

    public ObservableCollection<PairedDeviceViewModel> Paired { get; } = [];

    public bool HasPaired => Paired.Count > 0;

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Ui.Run(() => _ = RefreshAsync());
    }

    public async Task RefreshAsync()
    {
        Volatile.Write(ref _refreshQueued, 0);
        if (_disposed)
            return;
        try
        {
            var devices = await _core.Devices.GetPairedAsync(CancellationToken.None);
            if (_disposed)
                return;
            Paired.Clear();
            foreach (var device in devices)
                Paired.Add(new PairedDeviceViewModel(device, this));
            OnPropertyChanged(nameof(HasPaired));
        }
        catch (Exception e) when (e is not OutOfMemoryException && !_disposed)
        {
            _logger.LogWarning(e, "Device list could not be read");
        }
    }

    internal async Task ShowPermissionsAsync(PairedDeviceViewModel device) =>
        await _dialogs.ShowAsync(new PermissionsDialogViewModel(device.Name, device.CanSendToMe,
            allowed => RunAsync(() => _core.Devices.SetCanSendToMeAsync(device.Id, allowed, CancellationToken.None))));

    internal Task SetBlockedAsync(PairedDeviceViewModel device, bool blocked) =>
        RunAsync(() => _core.Devices.SetBlockedAsync(device.Id, blocked, CancellationToken.None));

    internal async Task RemoveAsync(PairedDeviceViewModel device)
    {
        var confirm = new ConfirmDialogViewModel(
            string.Format(CultureInfo.CurrentCulture, Strings.Confirm_RemoveTitle, device.Name), Strings.Confirm_RemoveText,
            Strings.Devices_Remove);
        await _dialogs.ShowAsync(confirm);
        if (confirm.Confirmed)
            await RunAsync(() => _core.Devices.RemoveAsync(device.Id, CancellationToken.None));
    }

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        try
        {
            await action();
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
        {
            Error = e.Message;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Pairing.Finished -= QueueRefresh;
        Pairing.Dispose();
        _core.Devices.Changed -= QueueRefresh;
        _core.Presence.Changed -= QueueRefresh;
    }
}
