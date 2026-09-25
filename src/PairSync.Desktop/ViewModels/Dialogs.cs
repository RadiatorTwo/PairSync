using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Desktop.Resources;

namespace PairSync.Desktop.ViewModels;

/// <summary>A modal dialog over the window content; <see cref="Close"/> ends it.</summary>
public abstract class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Closed => _closed.Task;

    protected void Close() => _closed.TrySetResult();
}

/// <summary>Shows one dialog at a time; later ones wait for their turn.</summary>
public sealed partial class DialogHost : ObservableObject
{
    private readonly Queue<DialogViewModel> _waiting = new();

    [ObservableProperty]
    private DialogViewModel? _current;

    public int Waiting => _waiting.Count;

    /// <summary>Completes when the dialog is closed.</summary>
    public Task ShowAsync(DialogViewModel dialog)
    {
        if (Current is null)
            Open(dialog);
        else
            _waiting.Enqueue(dialog);
        return dialog.Closed;
    }

    private void Open(DialogViewModel dialog)
    {
        Current = dialog;
        dialog.Closed.ContinueWith(_ => Ui.Run(() =>
        {
            if (!ReferenceEquals(Current, dialog))
                return;
            Current = null;
            // A dialog closed while it waited (e.g. a withdrawn offer) is skipped.
            while (_waiting.TryDequeue(out var next))
            {
                if (!next.Closed.IsCompleted)
                {
                    Open(next);
                    break;
                }
            }
        }), TaskScheduler.Default);
    }
}

/// <summary>"Remove laptop-win11?" with a confirming and a cancelling button.</summary>
public sealed partial class ConfirmDialogViewModel(string title, string text, string confirmLabel) : DialogViewModel
{
    public string Title { get; } = title;

    public string Text { get; } = text;

    public string ConfirmLabel { get; } = confirmLabel;

    public string CancelLabel => Strings.Dialog_Cancel;

    public bool Confirmed { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        Close();
    }

    [RelayCommand]
    private void Cancel() => Close();
}
