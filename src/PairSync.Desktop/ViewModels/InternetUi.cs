using PairSync.Application;
using PairSync.Desktop.Platform;

namespace PairSync.Desktop.ViewModels;

/// <summary>
/// The internet dialogs, shared by Overview, Devices and Settings: connect by code, NAT diagnostic, about relays.
/// Invitations found on the way go to the pairing card on Devices.
/// </summary>
public sealed class InternetUi(PairSyncCore core, DialogHost dialogs, IDesktopServices desktop, TimeProvider time)
{
    /// <summary>Set by the shell: shows Devices with its pairing card.</summary>
    internal Func<PairingViewModel>? ShowPairing { get; set; }

    /// <summary>"Connect via internet…" on the card of a paired device.</summary>
    public Task ConnectAsync(Guid deviceId, string deviceName) =>
        ShowAsync(ConnectDialogViewModel.ForDevice(core, desktop, time, this, deviceId, deviceName));

    /// <summary>"Paste connection code" (optionally with a text found elsewhere) or "Paste answer…" on a waiting card.</summary>
    public Task PasteCodeAsync(string? text = null, string? deviceName = null) =>
        ShowAsync(ConnectDialogViewModel.ForInput(core, desktop, time, this, text, deviceName));

    public Task ShowDiagnosticAsync() => ShowAsync(new NatDiagnosticDialogViewModel(core.Settings.Current.StunServers, desktop));

    public Task ShowAboutRelaysAsync() => ShowAsync(new AboutRelaysDialogViewModel());

    internal Task OpenInvitationAsync(string text) =>
        ShowPairing?.Invoke().ImportTextAsync(text) ?? Task.CompletedTask;

    internal Task ApplyInvitationAnswerAsync(string text) =>
        ShowPairing?.Invoke().ApplyAnswerTextAsync(text) ?? Task.CompletedTask;

    private async Task ShowAsync(DialogViewModel dialog)
    {
        try
        {
            await dialogs.ShowAsync(dialog);
        }
        finally
        {
            (dialog as IDisposable)?.Dispose();
        }
    }
}
