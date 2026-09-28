using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using PairSync.Storage.Settings;

namespace PairSync.Desktop.Themes;

/// <summary>Light and dark mode (DARK_MODE.md): the setting, the Fluent palettes and the colors of the icons.</summary>
internal static class AppThemes
{
    public static ThemeVariant VariantFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default, // follows the operating system, also when it switches
    };

    /// <summary>Switches at once; every color is a DynamicResource and follows.</summary>
    public static void Apply(Avalonia.Application app, AppTheme theme) => app.RequestedThemeVariant = VariantFor(theme);

    /// <summary>A color token of <paramref name="variant"/>, whatever the app shows right now.</summary>
    public static Color Token(Avalonia.Application app, string key, ThemeVariant variant) =>
        app.TryGetResource(key, variant, out var value) && value is Color color
            ? color
            : throw new KeyNotFoundException($"Color token {key} is missing for {variant}.");

    /// <summary>The Fluent controls that keep their templates (text boxes, scroll bars, menus) get their colors from the tokens.</summary>
    public static void UseTokenPalettes(Avalonia.Application app)
    {
        var fluent = app.Styles.OfType<FluentTheme>().Single();
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fluent.Palettes[variant] = new ColorPaletteResources
            {
                Accent = Token(app, "AccentColor", variant),
                RegionColor = Token(app, "BgColor", variant),
                BaseHigh = Token(app, "TextColor", variant),
            };
        }
    }

    /// <summary>
    /// Whether the taskbar or panel is dark: the tray icon follows it, not the app (DARK_MODE.md §3). Windows keeps the
    /// taskbar in its own setting; on Linux the panel follows the desktop theme.
    /// </summary>
    public static ThemeVariant TaskbarVariant(Avalonia.Application app)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("SystemUsesLightTheme") is int light)
                    return light == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // fall back to the system theme below
            }
        }
        return app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
