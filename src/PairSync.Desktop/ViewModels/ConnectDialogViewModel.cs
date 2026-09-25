using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application;
using PairSync.Application.Internet;
using PairSync.Application.Pairing;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Protocol;
using PairSync.Stun;

namespace PairSync.Desktop.ViewModels;

public enum ConnectStep
{
    /// <summary>The connection code is being created (ICE gathering, a few seconds).</summary>
    Preparing,

    /// <summary>Device A: the code is shown; the answer from the other device goes into the input.</summary>
    Code,

    /// <summary>Paste or open a code (connection code, answer, invitation).</summary>
    EnterCode,

    /// <summary>Device B: the answer code is shown; the connection comes up once the other side applies it.</summary>
    Answer,

    Connecting,

    Connected,

    Failed,
}

/// <summary>
/// "Connect via internet" (phase 2 block F): the code exchange with a paired device, as a dialog with the steps
/// code → answer → connection. On the other device the same dialog takes the code and shows the answer. Closing the
/// dialog keeps a waiting code; the device card shows it and takes the answer later.
/// </summary>
public sealed partial class ConnectDialogViewModel : DialogViewModel, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly IDesktopServices _desktop;
    private readonly TimeProvider _time;
    private readonly InternetUi _ui;
    private readonly DispatcherTimer _countdown;
    private readonly CancellationTokenSource _closing = new();
    private IssuedCode? _code;
    private Guid? _deviceId;
    private string? _deviceName;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparing), nameof(IsCode), nameof(IsEnterCode), nameof(IsAnswer), nameof(IsConnecting),
        nameof(IsConnected), nameof(IsFailed), nameof(ShowsCode), nameof(TakesInput), nameof(StepText), nameof(CloseLabel), nameof(InputPlaceholder))]
    private ConnectStep _step;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private Bitmap? _qrCode;

    [ObservableProperty]
    private string? _codeHint;

    [ObservableProperty]
    private string? _expiresText;

    [ObservableProperty]
    private bool _isExpired;

    [ObservableProperty]
    private string? _copiedText;

    [ObservableProperty]
    private string? _statusText;

    /// <summary>Why the last action did not work; the step stays.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _routeText;

    [ObservableProperty]
    private bool _canRunDiagnostic;

    private ConnectDialogViewModel(PairSyncCore core, IDesktopServices desktop, TimeProvider time, InternetUi ui)
    {
        _core = core;
        _desktop = desktop;
        _time = time;
        _ui = ui;
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateCountdown());
        _core.Internet.Changed += OnInternetChanged;
    }

    /// <summary>Device A: creates a connection code for <paramref name="deviceId"/>.</summary>
    internal static ConnectDialogViewModel ForDevice(PairSyncCore core, IDesktopServices desktop, TimeProvider time, InternetUi ui,
        Guid deviceId, string deviceName)
    {
        var dialog = new ConnectDialogViewModel(core, desktop, time, ui)
        {
            _deviceId = deviceId,
            _deviceName = deviceName,
            Title = string.Format(CultureInfo.CurrentCulture, Strings.Connect_Title, deviceName),
            StatusText = Strings.Connect_Preparing,
            Step = ConnectStep.Preparing,
        };
        _ = dialog.CreateCodeAsync();
        return dialog;
    }

    /// <summary>"Paste connection code", or "Paste answer…" on a waiting device card.</summary>
    internal static ConnectDialogViewModel ForInput(PairSyncCore core, IDesktopServices desktop, TimeProvider time, InternetUi ui,
        string? text = null, string? deviceName = null) =>
        new(core, desktop, time, ui)
        {
            _deviceName = deviceName,
            Title = deviceName is null ? Strings.Connect_PasteTitle : string.Format(CultureInfo.CurrentCulture, Strings.Connect_AnswerTitle, deviceName),
            Input = text ?? "",
            Step = ConnectStep.EnterCode,
        };

    public bool IsPreparing => Step == ConnectStep.Preparing;

    public bool IsCode => Step == ConnectStep.Code;

    public bool IsEnterCode => Step == ConnectStep.EnterCode;

    public bool IsAnswer => Step == ConnectStep.Answer;

    public bool IsConnecting => Step == ConnectStep.Connecting;

    public bool IsConnected => Step == ConnectStep.Connected;

    public bool IsFailed => Step == ConnectStep.Failed;

    /// <summary>A code of this device is on screen (QR, copy, save).</summary>
    public bool ShowsCode => Step is ConnectStep.Code or ConnectStep.Answer;

    /// <summary>The input for a code or an answer is on screen.</summary>
    public bool TakesInput => Step is ConnectStep.Code or ConnectStep.EnterCode;

    public string InputLabel => Step == ConnectStep.Code && _deviceName is not null
        ? string.Format(CultureInfo.CurrentCulture, Strings.Connect_PasteAnswer, _deviceName)
        : Strings.Connect_PasteCode;

    public string InputPlaceholder => Step == ConnectStep.Code ? Strings.Pair_AnswerPlaceholder : Strings.Connect_Placeholder;

    public string StepText => string.Format(CultureInfo.CurrentCulture, Strings.Connect_Step, Step switch
    {
        ConnectStep.Preparing or ConnectStep.Code or ConnectStep.EnterCode => 1,
        ConnectStep.Answer => 2,
        _ => 3,
    });

    public string CloseLabel => Step is ConnectStep.Connected or ConnectStep.Failed ? Strings.Dialog_Done : Strings.Dialog_Close;

    private async Task CreateCodeAsync()
    {
        try
        {
            ShowCode(await _core.Internet.CreateCodeAsync(_deviceId!.Value, _closing.Token), ConnectStep.Code,
                string.Format(CultureInfo.CurrentCulture, Strings.Connect_CodeHint, _deviceName));
            OnPropertyChanged(nameof(InputLabel));
        }
        catch (InternetConnectException e)
        {
            Fail(e.Message, e.Failure);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowCode(IssuedCode code, ConnectStep step, string hint)
    {
        if (_disposed)
            return;
        _code = code;
        QrCode = Codes.QrCode(code.Text);
        CodeHint = hint;
        CopiedText = null;
        IsExpired = false;
        Message = null;
        UpdateCountdown();
        _countdown.Start();
        Step = step;
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_code is null)
            return;
        await _desktop.CopyTextAsync(_code.Text);
        CopiedText = Strings.Pair_Copied;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_code is null)
            return;
        if (await _desktop.PickSaveFileAsync(_code.SuggestedFileName(_core.Settings.Current.EffectiveDeviceName), ConnectCodec.FileExtension) is { } path)
            await File.WriteAllTextAsync(path, _code.Text);
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        try
        {
            if (await Codes.LoadFileAsync(_desktop, ConnectCodec.FileExtension) is { } text)
            {
                Input = text;
                await SubmitAsync();
            }
        }
        catch (IOException e)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Code_FileFailed, e.Message);
        }
    }

    /// <summary>"Connect": applies the pasted answer, answers a pasted code, or hands an invitation to pairing.</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        var text = Input.Trim();
        Message = null;
        switch (Codes.Classify(text))
        {
            case CodeKind.ConnectionCode when Step == ConnectStep.EnterCode:
                await AnswerAsync(text);
                break;
            case CodeKind.Answer:
                await ApplyAnswerAsync(text);
                break;
            case CodeKind.Invitation when Step == ConnectStep.EnterCode:
                Close();
                await _ui.OpenInvitationAsync(text);
                break;
            case CodeKind.ConnectionCode:
                Message = Strings.Connect_NotAnAnswer;
                break;
            default:
                Message = Step == ConnectStep.Code ? Strings.Connect_NotAnAnswer : Strings.Connect_NotACode;
                break;
        }
    }

    private async Task AnswerAsync(string text)
    {
        try
        {
            var (device, answer) = await _core.Internet.AnswerCodeAsync(text, _closing.Token);
            _deviceId = device.Id;
            _deviceName = device.Name;
            Title = string.Format(CultureInfo.CurrentCulture, Strings.Connect_Title, device.Name);
            Input = "";
            ShowCode(answer, ConnectStep.Answer, string.Format(CultureInfo.CurrentCulture, Strings.Connect_AnswerHint, device.Name));
            StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Connect_WaitingForApply, device.Name);
        }
        catch (InvalidConnectCodeException e)
        {
            Message = e.Message;
        }
        catch (InternetConnectException e)
        {
            Fail(e.Message, e.Failure);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyAnswerAsync(string text)
    {
        AnswerTarget target;
        try
        {
            target = _core.Internet.TargetOfAnswer(text);
        }
        catch (InvalidConnectCodeException e)
        {
            Message = e.Message;
            return;
        }
        if (target == AnswerTarget.Invitation)
        {
            // The answer to an internet invitation: pairing continues on Devices.
            Close();
            await _ui.ApplyInvitationAnswerAsync(text);
            return;
        }

        var previous = Step;
        _countdown.Stop();
        StatusText = _deviceName is null ? Strings.Connect_Connecting : string.Format(CultureInfo.CurrentCulture, Strings.Connect_ConnectingTo, _deviceName);
        Step = ConnectStep.Connecting;
        try
        {
            var device = await _core.Internet.ApplyAnswerAsync(text, _closing.Token);
            _deviceId = device.Id;
            _deviceName = device.Name;
            ShowConnected();
        }
        catch (InvalidConnectCodeException e)
        {
            Message = e.Message;
            Step = previous;
            if (previous == ConnectStep.Code)
                _countdown.Start();
        }
        catch (InternetConnectException e)
        {
            Fail(e.Message, e.Failure);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnInternetChanged() => Ui.Run(() =>
    {
        if (_disposed || _deviceId is not { } id || Step is not (ConnectStep.Answer or ConnectStep.Connecting))
            return;
        switch (_core.Internet.StatusOf(id))
        {
            case { Phase: InternetLinkPhase.Connected }:
                ShowConnected();
                break;
            case { Phase: InternetLinkPhase.Failed } failed when Step == ConnectStep.Answer:
                Fail(failed.Error ?? Strings.Connect_Failed, failed.Failure);
                break;
        }
    });

    private void ShowConnected()
    {
        _countdown.Stop();
        var status = _deviceId is { } id ? _core.Internet.StatusOf(id) : null;
        StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Connect_Connected, _deviceName);
        RouteText = Codes.RouteLine(status?.Route);
        QrCode = null;
        Step = ConnectStep.Connected;
    }

    private void Fail(string message, ConnectFailureReason? failure)
    {
        if (_disposed)
            return;
        _countdown.Stop();
        QrCode = null;
        StatusText = message;
        CanRunDiagnostic = failure is not null;
        Step = ConnectStep.Failed;
    }

    [RelayCommand]
    private async Task RunDiagnosticAsync()
    {
        Close();
        await _ui.ShowDiagnosticAsync();
    }

    [RelayCommand]
    private async Task AboutRelaysAsync()
    {
        Close();
        await _ui.ShowAboutRelaysAsync();
    }

    /// <summary>Closes the dialog. A waiting code stays valid; the device card shows it.</summary>
    [RelayCommand]
    private void Dismiss() => Close();

    private void UpdateCountdown()
    {
        if (_code is null)
            return;
        var left = _code.ExpiresAtUtc - _time.GetUtcNow().UtcDateTime;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Stop();
            IsExpired = true;
            ExpiresText = Strings.Pair_Expired;
            return;
        }
        ExpiresText = string.Format(CultureInfo.CurrentCulture, Strings.Pair_Expires, Codes.Countdown(left));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _countdown.Stop();
        _core.Internet.Changed -= OnInternetChanged;
        // Only the wait on screen ends; an issued code or a connection in progress goes on (the card shows it).
        _closing.Cancel();
        _closing.Dispose();
    }
}
