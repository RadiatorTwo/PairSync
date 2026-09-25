using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PairSync.Desktop.Controls;

/// <summary>
/// A bordered panel. Classes: <c>connected</c> (Frame border and marks), <c>offline</c> (Hairline, muted text),
/// <c>found</c> (dashed Frame border), <c>marked</c> (marks only), <c>dialog</c> (dialog frame with shadow).
/// </summary>
public class Card : ContentControl
{
    public static readonly StyledProperty<bool> ShowMarksProperty =
        AvaloniaProperty.Register<Card, bool>(nameof(ShowMarks));

    public static readonly StyledProperty<IBrush?> MarksBrushProperty =
        AvaloniaProperty.Register<Card, IBrush?>(nameof(MarksBrush));

    public static readonly StyledProperty<bool> IsDashedProperty =
        AvaloniaProperty.Register<Card, bool>(nameof(IsDashed));

    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        Border.BoxShadowProperty.AddOwner<Card>();

    public bool ShowMarks
    {
        get => GetValue(ShowMarksProperty);
        set => SetValue(ShowMarksProperty, value);
    }

    /// <summary>Accent by default; Accent900 for the "Runs programs" panel.</summary>
    public IBrush? MarksBrush
    {
        get => GetValue(MarksBrushProperty);
        set => SetValue(MarksBrushProperty, value);
    }

    /// <summary>Dashed border for devices found on the LAN but not paired.</summary>
    public bool IsDashed
    {
        get => GetValue(IsDashedProperty);
        set => SetValue(IsDashedProperty, value);
    }

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }
}
