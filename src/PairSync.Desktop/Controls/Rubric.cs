using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media.TextFormatting;

namespace PairSync.Desktop.Controls;

/// <summary>
/// Section label: Barlow Condensed 14 px, upper case, tracking 0.1 em, Accent700. Upper-casing happens at layout,
/// so translations keep their normal spelling.
/// </summary>
public sealed class Rubric : TextBlock
{
    protected override TextLayout CreateTextLayout(string? text) =>
        base.CreateTextLayout(text?.ToUpper(CultureInfo.CurrentUICulture));
}
