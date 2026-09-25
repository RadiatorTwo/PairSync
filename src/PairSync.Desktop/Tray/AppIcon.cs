using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PairSync.Desktop.Tray;

/// <summary>
/// The PairSync mark, drawn at runtime: an outlined square (Accent900) paired with a filled one (Accent).
/// Used for the tray and the window. macOS later needs a monochrome template icon.
/// </summary>
internal static class AppIcon
{
    public static WindowIcon Create(int size = 64)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var unit = size / 32.0;
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x30, 0x43)), 2.5 * unit);
            context.DrawRectangle(null, outline, new Rect(3 * unit, 3 * unit, 17 * unit, 17 * unit));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x59, 0x80, 0xA6)), null,
                new Rect(12 * unit, 12 * unit, 18 * unit, 18 * unit));
        }
        return new WindowIcon(bitmap);
    }
}
