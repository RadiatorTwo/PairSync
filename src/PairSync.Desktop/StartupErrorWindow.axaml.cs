using Avalonia.Controls;

namespace PairSync.Desktop;

/// <summary>
/// Shown instead of the main window when the core cannot start (identity key unreadable, database from a newer
/// version): the reason and the data folder, never a silent crash.
/// </summary>
public sealed partial class StartupErrorWindow : Window
{
    public StartupErrorWindow() => InitializeComponent();

    public StartupErrorWindow(string message, string dataFolder) : this()
    {
        this.FindControl<TextBlock>("Message")!.Text = message;
        this.FindControl<TextBlock>("Folder")!.Text = dataFolder;
        this.FindControl<Button>("Quit")!.Click += (_, _) => Close();
        this.FindControl<Button>("OpenFolder")!.Click += async (_, _) =>
        {
            if (Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(dataFolder));
        };
    }
}
