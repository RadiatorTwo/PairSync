using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application.Transfers;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Domain;

namespace PairSync.Desktop.ViewModels;

/// <summary>
/// "laptop-win11 wants to send you 3 items": the receiver confirms target and scope before anything is written.
/// Closes by itself when the sender withdraws the offer.
/// </summary>
public sealed partial class IncomingTransferViewModel : DialogViewModel
{
    private const int ShownEntries = 5;

    private readonly IncomingTransfer _offer;
    private readonly IDesktopServices _desktop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceWarning))]
    private string _targetFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeepBoth), nameof(IsReplace), nameof(IsSkip))]
    private ExistingFilePolicy _policy;

    public IncomingTransferViewModel(IncomingTransfer offer, IDesktopServices desktop)
    {
        _offer = offer;
        _desktop = desktop;
        _targetFolder = offer.SuggestedFolder;
        _policy = offer.ProposedPolicy;
        var culture = CultureInfo.CurrentCulture;
        Title = string.Format(culture, Strings.Incoming_Title, offer.PeerName, Format.Items(offer.Entries.Count));
        Summary = $"{Format.Files(offer.FileCount)} · {Format.Bytes(offer.TotalBytes)}";
        Entries = [.. offer.Entries.Take(ShownEntries).Select(e =>
            $"{e.Name}{(e.IsFolder ? "/" : "")} · {(e.IsFolder ? Format.Files(e.FileCount) + " · " : "")}{Format.Bytes(e.Size)}")];
        More = offer.Entries.Count > ShownEntries ? string.Format(culture, Strings.Incoming_More, offer.Entries.Count - ShownEntries) : null;
        // The sender gave up (or the offer was answered elsewhere): the dialog goes away.
        _ = offer.Closed.ContinueWith(_ => Ui.Run(Close), TaskScheduler.Default);
    }

    public Guid JobId => _offer.JobId;

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<string> Entries { get; }

    public string? More { get; }

    /// <summary>Free space is only known for the suggested folder.</summary>
    public string? SpaceWarning =>
        TargetFolder == _offer.SuggestedFolder && _offer.FreeBytes is { } free && free < _offer.TotalBytes
            ? string.Format(CultureInfo.CurrentCulture, Strings.Incoming_NoSpace, Format.Bytes(free))
            : null;

    public bool IsKeepBoth
    {
        get => Policy == ExistingFilePolicy.KeepBoth;
        set { if (value) Policy = ExistingFilePolicy.KeepBoth; }
    }

    public bool IsReplace
    {
        get => Policy == ExistingFilePolicy.Replace;
        set { if (value) Policy = ExistingFilePolicy.Replace; }
    }

    public bool IsSkip
    {
        get => Policy == ExistingFilePolicy.Skip;
        set { if (value) Policy = ExistingFilePolicy.Skip; }
    }

    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        var start = Directory.Exists(TargetFolder) ? TargetFolder : Path.GetDirectoryName(TargetFolder);
        if (await _desktop.PickFolderAsync(start) is { } folder)
            TargetFolder = folder;
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        await _offer.AcceptAsync(TargetFolder, Policy);
        Close();
    }

    [RelayCommand]
    private async Task DeclineAsync()
    {
        await _offer.DeclineAsync();
        Close();
    }
}
