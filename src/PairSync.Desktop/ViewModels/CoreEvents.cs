using PairSync.Application;
using PairSync.Application.Pairing;
using PairSync.Application.Sync;
using PairSync.Desktop.Platform;

namespace PairSync.Desktop.ViewModels;

/// <summary>
/// Requests from other devices that need the user: a pairing request opens the code step on Devices and brings the
/// window to the front; a sync profile offer opens its dialog on Syncs. Transfers from paired devices are accepted
/// without asking.
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
        _core.Pairing.IncomingRequest += OnPairingRequest;
        _core.Sync.OfferReceived += OnProfileOffer;
        _core.Claude.Incoming += OnClaudeIncoming;
    }

    /// <summary>Another device starts changing Claude Code here: show what happens (installers may ask for rights on this screen).</summary>
    private void OnClaudeIncoming(PairSync.Application.Claude.ClaudeIncomingEvent e)
    {
        if (e.Stage != PairSync.Application.Claude.ClaudeIncomingStage.Started)
            return;
        Ui.Run(() =>
        {
            _desktop.RevealWindow();
            _shell.Navigate(AppPage.ClaudeCode);
        });
    }

    private void OnProfileOffer(IncomingProfileOffer offer) => Ui.Run(() =>
    {
        _desktop.RevealWindow();
        _shell.Navigate(AppPage.Syncs);
        _ = _shell.Page<SyncsViewModel>().ShowOfferAsync(offer);
    });

    private void OnPairingRequest(PairingSession session) => Ui.Run(() =>
    {
        _desktop.RevealWindow();
        _shell.Navigate(AppPage.Devices);
        _shell.Page<DevicesViewModel>().Pairing.ShowIncoming(session);
    });

    public void Dispose()
    {
        _core.Pairing.IncomingRequest -= OnPairingRequest;
        _core.Sync.OfferReceived -= OnProfileOffer;
        _core.Claude.Incoming -= OnClaudeIncoming;
    }
}
