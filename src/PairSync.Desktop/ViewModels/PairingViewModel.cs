using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application;
using PairSync.Application.Internet;
using PairSync.Application.Pairing;
using PairSync.Application.Presence;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Protocol;

namespace PairSync.Desktop.ViewModels;

public enum PairingStep
{
    /// <summary>Step 1: devices on the LAN, create or import an invitation.</summary>
    Start,

    /// <summary>Step 2: the invitation is shown and waits to be redeemed (internet: waits for the answer code).</summary>
    Invitation,

    /// <summary>Internet, invited device: the answer code is shown and has to reach the inviting device.</summary>
    AnswerCode,

    /// <summary>Connecting to the other device (or preparing an invitation).</summary>
    Connecting,

    /// <summary>Last step: both screens show the security code.</summary>
    Code,
}

/// <summary>
/// The pairing card on Devices (plan §5, work package E; phase 2 block F): LAN or invitation, then the security code.
/// An internet invitation adds one step: the invited device shows an answer code, the inviting device pastes it.
/// Nothing is stored unless both users confirm; cancel and timeout keep the lists as they were.
/// </summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly IDesktopServices _desktop;
    private readonly TimeProvider _time;
    private readonly InternetUi? _internet;
    private readonly DispatcherTimer _countdown;
    private IssuedInvitation? _invitation;
    private IssuedCode? _answer;
    private CancellationTokenSource? _wait;
    private PairingSession? _session;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText), nameof(IsStart), nameof(IsInvitation), nameof(IsAnswerCode), nameof(IsConnecting),
        nameof(IsCode), nameof(ShowsQrCode))]
    private PairingStep _step = PairingStep.Start;

    /// <summary>The current pairing runs over the internet (one more step: the answer code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    private bool _isInternet;

    [ObservableProperty]
    private string _invitationInput = "";

    /// <summary>Internet invitation: the answer code of the invited device.</summary>
    [ObservableProperty]
    private string _answerInput = "";

    [ObservableProperty]
    private Bitmap? _qrCode;

    [ObservableProperty]
    private string? _qrHint;

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

    /// <summary>Why the pasted answer was not accepted; the invitation stays.</summary>
    [ObservableProperty]
    private string? _answerMessage;

    [ObservableProperty]
    private string? _copiedText;

    /// <param name="internet">Opens connection codes pasted here; null when the dialogs are not available (tests).</param>
    public PairingViewModel(PairSyncCore core, IDesktopServices desktop, TimeProvider time, InternetUi? internet = null)
    {
        _core = core;
        _desktop = desktop;
        _time = time;
        _internet = internet;
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
        PairingStep.Invitation or PairingStep.AnswerCode => 2,
        PairingStep.Connecting => IsInternet ? 3 : 2,
        _ => IsInternet ? 4 : 3,
    }, IsInternet ? 4 : 3);

    public bool IsStart => Step == PairingStep.Start;

    public bool IsInvitation => Step == PairingStep.Invitation;

    public bool IsAnswerCode => Step == PairingStep.AnswerCode;

    public bool IsConnecting => Step == PairingStep.Connecting;

    public bool IsCode => Step == PairingStep.Code;

    public bool ShowsQrCode => Step is PairingStep.Invitation or PairingStep.AnswerCode;

    /// <summary>Paired or canceled: the device lists may have changed.</summary>
    public event Action? Finished;

    /// <summary>"Pair a new device": an internet invitation when STUN servers are set, otherwise a LAN invitation.</summary>
    [RelayCommand]
    private async Task CreateInvitationAsync()
    {
        Message = null;
        RevokeInvitation();
        var overInternet = _core.Internet.CanInviteOverInternet;
        if (overInternet)
        {
            // Gathering the candidates takes a few seconds.
            ConnectingText = Strings.Pair_Preparing;
            IsInternet = true;
            Step = PairingStep.Connecting;
        }
        IssuedInvitation invitation;
        try
        {
            invitation = await _core.Pairing.CreateInvitationAsync(CancellationToken.None, overInternet);
        }
        catch (PairingException e)
        {
            Reset(string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message));
            return;
        }
        if (_disposed || Step is PairingStep.Code)
        {
            _core.Pairing.RevokeInvitation(invitation);
            return;
        }
        _invitation = invitation;
        IsInternet = overInternet;
        ShowQrCode(invitation.Text, invitation.ExpiresAtUtc, overInternet ? Strings.Pair_QrHintInternet : Strings.Pair_QrHint);
        AnswerInput = "";
        AnswerMessage = null;
        Step = PairingStep.Invitation;
    }

    public bool InvitationIsInternet => IsInternet && _invitation is not null;

    [RelayCommand]
    private async Task CopyInvitationAsync()
    {
        if ((_invitation?.Text ?? _answer?.Text) is not { } text)
            return;
        await _desktop.CopyTextAsync(text);
        CopiedText = Strings.Pair_Copied;
    }

    [RelayCommand]
    private async Task SaveInvitationAsync()
    {
        var name = _core.Settings.Current.EffectiveDeviceName;
        var (text, fileName, extension) = _invitation is { } invitation
            ? (invitation.Text, invitation.SuggestedFileName(name), InvitationCodec.FileExtension)
            : _answer is { } answer
                ? (answer.Text, answer.SuggestedFileName(name), ConnectCodec.FileExtension)
                : (null, null, null);
        if (text is null)
            return;
        if (await _desktop.PickSaveFileAsync(fileName!, extension!) is { } path)
            await File.WriteAllTextAsync(path, text);
    }

    /// <summary>"Connect" under an invitation, a connection code or an answer code pasted on step 1.</summary>
    [RelayCommand]
    private Task ImportAsync() => ImportTextAsync(InvitationInput);

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        string? text;
        try
        {
            text = await Codes.LoadFileAsync(_desktop, InvitationCodec.FileExtension);
        }
        catch (IOException e)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message);
            return;
        }
        if (text is not null)
            await ImportTextAsync(text);
    }

    /// <summary>Takes any PairSync code: invitations pair here, connection codes and answers go where they belong.</summary>
    internal async Task ImportTextAsync(string text)
    {
        if (Step is PairingStep.Connecting or PairingStep.Code or PairingStep.AnswerCode)
            return;
        Message = null;
        switch (Codes.Classify(text))
        {
            case CodeKind.Invitation:
                await AcceptInvitationAsync(text.Trim());
                break;
            case CodeKind.Answer:
                await ApplyAnswerTextAsync(text.Trim());
                break;
            case CodeKind.ConnectionCode when _internet is not null:
                InvitationInput = "";
                await _internet.PasteCodeAsync(text.Trim());
                break;
            default:
                Message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, Strings.Connect_NotACode);
                break;
        }
    }

    private async Task AcceptInvitationAsync(string text)
    {
        ReceivedInvitation invitation;
        try
        {
            invitation = _core.Pairing.ReadInvitation(text);
        }
        catch (PairingException e)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message);
            return;
        }

        RevokeInvitation();
        IsInternet = false;
        ConnectingText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Connecting, invitation.DeviceName);
        Step = PairingStep.Connecting;
        _wait = new CancellationTokenSource();
        var wait = _wait;
        try
        {
            // The LAN part is quick; the internet part waits until the other side applied the answer (or Close).
            using (var lan = CancellationTokenSource.CreateLinkedTokenSource(wait.Token))
            {
                if (invitation.Offer is null)
                    lan.CancelAfter(TimeSpan.FromSeconds(30));
                var pairing = await _core.Pairing.AcceptInvitationAsync(invitation, lan.Token);
                if (pairing.Answer is { } answer)
                {
                    _answer = answer;
                    IsInternet = true;
                    ShowQrCode(answer.Text, answer.ExpiresAtUtc,
                        string.Format(CultureInfo.CurrentCulture, Strings.Pair_AnswerHint, invitation.DeviceName));
                    ConnectingText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_WaitingForApply, invitation.DeviceName);
                    Step = PairingStep.AnswerCode;
                }
                var session = await pairing.Session.WaitAsync(wait.Token);
                if (!ReferenceEquals(_wait, wait) || _disposed)
                {
                    _ = session.CancelAsync("the pairing was closed");
                    return;
                }
                ShowSession(session);
            }
        }
        catch (Exception e) when (e is PairingException or IOException or OperationCanceledException or TimeoutException)
        {
            if (ReferenceEquals(_wait, wait) && !_disposed)
                Reset(wait.IsCancellationRequested && e is OperationCanceledException
                    ? null
                    : string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed,
                        e is OperationCanceledException ? Strings.Pair_NoAnswer : e.Message));
        }
        finally
        {
            if (ReferenceEquals(_wait, wait))
                _wait = null;
            wait.Dispose();
        }
    }

    /// <summary>Internet invitation, inviting device: "Connect" with the pasted answer.</summary>
    [RelayCommand]
    private Task ApplyAnswerAsync() => ApplyAnswerTextAsync(AnswerInput.Trim());

    [RelayCommand]
    private async Task OpenAnswerFileAsync()
    {
        try
        {
            if (await Codes.LoadFileAsync(_desktop, ConnectCodec.FileExtension) is { } text)
            {
                AnswerInput = text;
                await ApplyAnswerTextAsync(text.Trim());
            }
        }
        catch (IOException e)
        {
            AnswerMessage = string.Format(CultureInfo.CurrentCulture, Strings.Code_FileFailed, e.Message);
        }
    }

    /// <summary>
    /// The answer to this device's internet invitation: connects, then the other device starts the pairing and the code
    /// step follows (<see cref="ShowIncoming"/>). An answer to a connection code goes to the connect dialog.
    /// </summary>
    internal async Task ApplyAnswerTextAsync(string text)
    {
        AnswerMessage = null;
        try
        {
            if (_core.Internet.TargetOfAnswer(text) == AnswerTarget.ConnectionCode)
            {
                if (_internet is not null)
                    await _internet.PasteCodeAsync(text);
                return;
            }
        }
        catch (InvalidConnectCodeException e)
        {
            ShowAnswerProblem(e.Message);
            return;
        }
        if (Step is PairingStep.Code or PairingStep.Connecting)
            return;

        _countdown.Stop();
        IsInternet = true;
        ConnectingText = Strings.Connect_Connecting;
        Step = PairingStep.Connecting;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var answer = await _core.Pairing.ApplyInvitationAnswerAsync(text, timeout.Token);
            // The invitation is redeemed by the pairing request that follows; it must not be revoked now.
            _invitation = null;
            if (Step == PairingStep.Connecting && _session is null)
                ConnectingText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_ConnectedWaiting, answer.DeviceName);
        }
        catch (InvalidInvitationException e) when (_invitation is not null)
        {
            // Wrong or damaged answer: the invitation stays open for the right one.
            Step = PairingStep.Invitation;
            _countdown.Start();
            ShowAnswerProblem(e.Message);
        }
        catch (Exception e) when (e is PairingException or OperationCanceledException)
        {
            _invitation = null;
            Reset(string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, e.Message));
        }
    }

    private void ShowAnswerProblem(string message)
    {
        if (Step == PairingStep.Invitation)
            AnswerMessage = message;
        else
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Failed, message);
    }

    /// <summary>"Pair…" on a device found on the LAN (here or on Overview).</summary>
    public Task PairWithAsync(NearbyDevice device) =>
        ConnectAsync(device.Name, ct => _core.Pairing.PairAsync(device, ct));

    private async Task ConnectAsync(string name, Func<CancellationToken, Task<PairingSession>> pair)
    {
        if (Step is PairingStep.Connecting or PairingStep.Code or PairingStep.AnswerCode)
            return;
        Message = null;
        try
        {
            RevokeInvitation();
            IsInternet = false;
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

    /// <summary>The other device found this one, redeemed its invitation, or paired over the internet connection.</summary>
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
        _countdown.Stop();
        QrCode = null;
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
            message = string.Format(CultureInfo.CurrentCulture, IsInternet ? Strings.Pair_DoneInternet : Strings.Pair_Done, device.Name);
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

    /// <summary>Closes the invitation or the answer (neither can be used anymore) and goes back to step 1.</summary>
    [RelayCommand]
    private void Close()
    {
        RevokeInvitation();
        Reset(null);
    }

    private void Reset(string? message)
    {
        CancelWait();
        _countdown.Stop();
        _answer = null;
        QrCode = null;
        SecurityCode = null;
        WaitingText = null;
        InvitationInput = "";
        AnswerInput = "";
        AnswerMessage = null;
        Message = message;
        Step = PairingStep.Start;
        IsInternet = false;
    }

    private void CancelWait()
    {
        if (_wait is { } wait)
        {
            _wait = null;
            wait.Cancel();
        }
    }

    private void ShowQrCode(string text, DateTime expiresAtUtc, string hint)
    {
        QrCode = Codes.QrCode(text);
        QrHint = hint;
        CopiedText = null;
        IsExpired = false;
        _expiresAtUtc = expiresAtUtc;
        UpdateCountdown();
        _countdown.Start();
        OnPropertyChanged(nameof(InvitationIsInternet));
    }

    private DateTime? _expiresAtUtc;

    private void RevokeInvitation()
    {
        if (_invitation is { } invitation)
            _core.Pairing.RevokeInvitation(invitation);
        _invitation = null;
        _countdown.Stop();
    }

    private void UpdateCountdown()
    {
        if (_expiresAtUtc is not { } expires)
            return;
        var left = expires - _time.GetUtcNow().UtcDateTime;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Stop();
            IsExpired = true;
            ExpiresText = Strings.Pair_Expired;
            return;
        }
        ExpiresText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Expires, Codes.Countdown(left));
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
        CancelWait();
        if (_session is { } session)
            _ = session.CancelAsync("the window was closed");
    }
}
