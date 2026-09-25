using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PairSync.Desktop.Controls;

/// <summary>
/// The four "+" marks at the corners of highlighted objects (connected device cards, primary button, dialog,
/// pairing card). Used as an overlay in control templates; draws outside its bounds and ignores input.
/// </summary>
public sealed class RegistrationMarks : Control
{
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<RegistrationMarks, IBrush?>(nameof(Brush));

    /// <summary>Length of each arm; the handoff draws a 12 px "+" glyph, about 7 px wide.</summary>
    private const double Arm = 3.5;

    static RegistrationMarks()
    {
        AffectsRender<RegistrationMarks>(BrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<RegistrationMarks>(false);
        FocusableProperty.OverrideDefaultValue<RegistrationMarks>(false);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush)
            return;
        var pen = new Pen(brush, 1);
        // Centered on the 1 px border line of each corner, on pixel centers so the lines stay sharp.
        var left = 0.5;
        var top = 0.5;
        var right = Math.Max(left, Math.Round(Bounds.Width) - 0.5);
        var bottom = Math.Max(top, Math.Round(Bounds.Height) - 0.5);
        DrawPlus(context, pen, left, top);
        DrawPlus(context, pen, right, top);
        DrawPlus(context, pen, left, bottom);
        DrawPlus(context, pen, right, bottom);
    }

    private static void DrawPlus(DrawingContext context, Pen pen, double x, double y)
    {
        context.DrawLine(pen, new Point(x - Arm, y), new Point(x + Arm, y));
        context.DrawLine(pen, new Point(x, y - Arm), new Point(x, y + Arm));
    }
}
