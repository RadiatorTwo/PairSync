using CommunityToolkit.Mvvm.ComponentModel;
using PairSync.Desktop.Resources;

namespace PairSync.Desktop.ViewModels;

/// <summary>The screens in the sidebar, in navigation order.</summary>
public enum AppPage
{
    Overview,
    Send,
    Syncs,
    ClaudeCode,
    Devices,
    Settings,
}

/// <summary>A screen of the main window.</summary>
public abstract class PageViewModel(AppPage page) : ObservableObject
{
    public AppPage Page { get; } = page;

    /// <summary>Sidebar entry and window title ("PairSync — Overview").</summary>
    public string NavTitle { get; } = page switch
    {
        AppPage.Overview => Strings.Nav_Overview,
        AppPage.Send => Strings.Nav_Send,
        AppPage.Syncs => Strings.Nav_Syncs,
        AppPage.ClaudeCode => Strings.Nav_ClaudeCode,
        AppPage.Devices => Strings.Nav_Devices,
        AppPage.Settings => Strings.Nav_Settings,
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>Heading of the screen.</summary>
    public virtual string Title => NavTitle;
}

/// <summary>A screen that only shows its heading and a note, for features of later phases.</summary>
public sealed class PlaceholderViewModel(AppPage page, string? title = null, string? message = null) : PageViewModel(page)
{
    public override string Title => title ?? NavTitle;

    public string? Message { get; } = message;
}

/// <summary>An option of a segment control; shows its label.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
