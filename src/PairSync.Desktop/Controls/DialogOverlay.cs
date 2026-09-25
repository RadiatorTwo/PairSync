using Avalonia;
using Avalonia.Controls;

namespace PairSync.Desktop.Controls;

/// <summary>
/// Modal dialog in the style of the mockup: scrim over the window content, a 680 px frame with marks in the middle.
/// Visible while it has content; the shell puts the active dialog's view model here.
/// </summary>
public sealed class DialogOverlay : ContentControl
{
    static DialogOverlay() => IsVisibleProperty.OverrideDefaultValue<DialogOverlay>(false);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
            IsVisible = change.NewValue is not null;
    }
}
