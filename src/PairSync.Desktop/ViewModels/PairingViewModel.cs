using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application;
using PairSync.Application.Pairing;
using PairSync.Application.Presence;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Protocol;
using QRCoder;

namespace PairSync.Desktop.ViewModels;

public enum PairingStep
{
    /// <summary>Step 1: devices on the LAN, create or import an invitation.</summary>
    Start,

    /// <summary>Step 2: the invitation is shown and waits to be redeemed.</summary>
    Invitation,

    /// <summary>Step 2: connecting to the other device.</summary>
    Connecting,

    /// <summary>Step 3: both screens show the security code.</summary>
    Code,
}

/// <summary>
/// The pairing card on Devices (plan §5, work package E): LAN or invitation, then the security code. Nothing is
/// stored unless both users confirm; cancel and timeout keep the lists as they were.
/// </summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly IDesktopServices _desktop;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _countdown;
    private IssuedInvitation? _invitation;
    private PairingSession? _session;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText), nameof(IsStart), nameof(IsInvitation), nameof(IsConnecting), nameof(IsCode))]
    private PairingStep _step = PairingStep.Start;

    [ObservableProperty]
    private string _invitationInput = "";

    [ObservableProperty]
    private Bitmap? _qrCode;

    [ObservableProperty]
    private string? _expiresText;

    [ObservableProperty]
    private bool _isExpired;

    [ObservableProperty]
    private string? _connectingText;

    [ObservableProperty]
    private string? _respondedText;

    [ObservableProperty]
    private string? _securityCode;

    [ObservableProperty]
    private string? _remoteFingerprint;

    /// <summary>This side confirmed; waiting for the other user.</summary>
    [ObservableProperty]
    private string? _waitingText;

    /// <summary>Result or error of the last attempt, shown on the start step.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _copiedText;

    public PairingViewModel(PairSyncCore core, IDesktopServices desktop, TimeProvider time)
    {
        _core = core;
        _desktop = desktop;
        _time = time;
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateCountdown());
        _core.Presence.Changed += OnPresenceChanged;
        LoadFound();
    }

    /// <summary>Unpaired devices announcing themselves on the LAN.</summary>
    public ObservableCollection<DeviceCardViewModel> Found { get; } = [];

    public bool HasFound => Found.Count > 0;

    public string StepText => string.Format(CultureInfo.CurrentCulture, Strings.Pair_Step, Step switch
    {
        PairingStep.Start => 1,
        PairingStep.Code => 3,
        _ => 2,
    });

    public bool IsStart => Step == PairingStep.Start;

    public bool IsInvitation => Step == PairingStep.Invitation;

    public bool IsConnecting => Step == PairingStep.Connecting;

    public bool IsCode => Step == PairingStep.Code;

    /// <summary>Paired or canceled: the device lists may have changed.</summary>
    public event Action? Finished;

    [RelayCommand]
    private void CreateInvitation()
    {
        Message = null;
        RevokeInvitation();
        _invitation = _core.Pairing.CreateInvitation();
        QrCode = CreateQrCode(_invitation.Text);
        CopiedText = null;
        IsExpired = false;
        UpdateCountdown();
        _countdown.Start();
        Step = PairingStep.Invitation;
    }

    [RelayCommand]
    private async Task CopyInvitationAsync()
    {
        if (_invitation is null)
            return;
        await _desktop.CopyTextAsync(_invitation.Text);
        CopiedText = Strings.Pair_Copied;
    }

    [RelayCommand]
    private async Task SaveInvitationAsync()
    {
        if (_invitation is null)
            return;
        var name = _invitation.SuggestedFileName(_core.Settings.Current.EffectiveDeviceName);
        if (await _desktop.PickSaveFileAsync(name, InvitationCodec.FileExtension) is { } path)
            await File.WriteAllTextAsync(path, _invitation.Text);
    }

    [RelayCommand]
    private Task ImportAsync() => ConnectAsync(() =>
    {
        var invitation = _core.Pairing.ReadInvitation(InvitationInput);
        return (invitation.DeviceName, ct => _core.Pairing.PairAsync(invitation, ct));
    });

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        if (await _desktop.PickOpenFileAsync(InvitationCodec.FileExtension) is not { } path)
            return;
        ReceivedInvitation invitation;
        try
        {
            invitation = await _core.Pairing.ReadInvitationFileAsync(path, CancellationToken.None);
        }
        catch (Exception e) when (e is PairingException or IOException or UnauthorizedAccessException)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message);
            return;
        }
        await ConnectAsync(() => (invitation.DeviceName, ct => _core.Pairing.PairAsync(invitation, ct)));
    }

    /// <summary>"Pair…" on a device found on the LAN (here or on Overview).</summary>
    public Task PairWithAsync(NearbyDevice device) =>
        ConnectAsync(() => (device.Name, ct => _core.Pairing.PairAsync(device, ct)));

    private async Task ConnectAsync(Func<(string Name, Func<CancellationToken, Task<PairingSession>> Pair)> start)
    {
        if (Step is PairingStep.Connecting or PairingStep.Code)
            return;
        Message = null;
        try
        {
            var (name, pair) = start();
            RevokeInvitation();
            ConnectingText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Connecting, name);
            Step = PairingStep.Connecting;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ShowSession(await pair(timeout.Token));
        }
        catch (Exception e) when (e is PairingException or IOException or OperationCanceledException or TimeoutException)
        {
            Reset(string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message));
        }
    }

    /// <summary>The other device found this one or redeemed its invitation.</summary>
    public void ShowIncoming(PairingSession session)
    {
        if (_session is not null)
        {
            // One pairing at a time on screen as well.
            _ = session.CancelAsync("the other device is already pairing");
            return;
        }
        RevokeInvitation();
        ShowSession(session);
    }

    private void ShowSession(PairingSession session)
    {
        _session = session;
        RespondedText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Responded, session.RemoteName);
        SecurityCode = session.SecurityCode;
        RemoteFingerprint = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Fingerprint, session.RemoteFingerprint.ToShortString());
        WaitingText = null;
        Step = PairingStep.Code;
        _ = ObserveAsync(session);
    }

    private async Task ObserveAsync(PairingSession session)
    {
        string message;
        try
        {
            var device = await session.Completion;
            message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Done, device.Name);
        }
        catch (PairingCanceledException e)
        {
            message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Canceled, e.Reason);
        }
        catch (PairingException e)
        {
            message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message);
        }
        await session.DisposeAsync();
        Ui.Run(() =>
        {
            if (_disposed || !ReferenceEquals(_session, session))
                return;
            _session = null;
            Reset(message);
            Finished?.Invoke();
        });
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_session is not { } session)
            return;
        WaitingText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_WaitingOther, session.RemoteName);
        try
        {
            await session.ConfirmAsync(CancellationToken.None);
        }
        catch (PairingException)
        {
            // Completion reports the outcome.
        }
    }

    [RelayCommand]
    private async Task DifferAsync()
    {
        if (_session is { } session)
            await session.CancelAsync();
    }

    /// <summary>Closes the invitation (it cannot be used anymore) and goes back to step 1.</summary>
    [RelayCommand]
    private void Close()
    {
        RevokeInvitation();
        Reset(null);
    }

    private void Reset(string? message)
    {
        _countdown.Stop();
        QrCode = null;
        SecurityCode = null;
        WaitingText = null;
        InvitationInput = "";
        Message = message;
        Step = PairingStep.Start;
    }

    private void RevokeInvitation()
    {
        if (_invitation is { } invitation)
            _core.Pairing.RevokeInvitation(invitation);
        _invitation = null;
        _countdown.Stop();
    }

    private void UpdateCountdown()
    {
        if (_invitation is null)
            return;
        var left = _invitation.ExpiresAtUtc - _time.GetUtcNow().UtcDateTime;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Stop();
            IsExpired = true;
            ExpiresText = Strings.Pair_Expired;
            return;
        }
        ExpiresText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Expires, $"{(int)left.TotalMinutes:00}:{left.Seconds:00}");
    }

    private static Bitmap CreateQrCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // Dark modules in Text (#1D1F20) on the page background, 1 px per module; the view scales without smoothing.
        var png = new PngByteQRCode(data).GetGraphic(1, [0x1D, 0x1F, 0x20], [0xF2, 0xF2, 0xF3]);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    private void OnPresenceChanged() => Ui.Run(() =>
    {
        if (!_disposed)
            LoadFound();
    });

    private void LoadFound()
    {
        Found.Clear();
        foreach (var device in _core.Presence.Devices.Where(d => d.State == PresenceState.Found))
            Found.Add(new DeviceCardViewModel(device, CardKind.Found, null, Strings.Card_Found, null, PairWithAsync));
        OnPropertyChanged(nameof(HasFound));
    }

    public void Dispose()
    {
        _disposed = true;
        _countdown.Stop();
        _core.Presence.Changed -= OnPresenceChanged;
        RevokeInvitation();
        if (_session is { } session)
            _ = session.CancelAsync("the window was closed");
    }
}
