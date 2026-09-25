using PairSync.Application;
using PairSync.Application.Pairing;
using PairSync.Application.Transfers;
using PairSync.Desktop.Platform;

namespace PairSync.Desktop.ViewModels;

/// <summary>
/// Requests from other devices that need the user: an offered transfer opens its dialog, a pairing request opens
/// the code step on Devices. Both bring the window to the front.
/// </summary>
public sealed class CoreEvents : IDisposable
{
    private readonly PairSyncCore _core;
    private readonly ShellViewModel _shell;
    private readonly IDesktopServices _desktop;

    public CoreEvents(PairSyncCore core, ShellViewModel shell, IDesktopServices desktop)
    {
        _core = core;
        _shell = shell;
        _desktop = desktop;
        _core.Transfers.IncomingOffer += OnIncomingOffer;
        _core.Pairing.IncomingRequest += OnPairingRequest;
    }

    private void OnIncomingOffer(IncomingTransfer offer) => Ui.Run(() =>
    {
        _desktop.RevealWindow();
        _ = _shell.Dialogs.ShowAsync(new IncomingTransferViewModel(offer, _desktop));
    });

    private void OnPairingRequest(PairingSession session) => Ui.Run(() =>
    {
        _desktop.RevealWindow();
        _shell.Navigate(AppPage.Devices);
        _shell.Page<DevicesViewModel>().Pairing.ShowIncoming(session);
    });

    public void Dispose()
    {
        _core.Transfers.IncomingOffer -= OnIncomingOffer;
        _core.Pairing.IncomingRequest -= OnPairingRequest;
    }
}
