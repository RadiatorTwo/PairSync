using Avalonia;
using Avalonia.Data.Converters;

namespace PairSync.Desktop.ViewModels;

/// <summary>Small converters for spacing that depends on state.</summary>
public static class Converters
{
    /// <summary>A found card has no state row; its text keeps 10 px above and below (mockup).</summary>
    public static readonly IValueConverter FoundTextMargin =
        new FuncValueConverter<bool, Thickness>(found => found ? new Thickness(0, 10) : default);
}
