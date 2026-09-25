using Avalonia.Controls;
using PairSync.Desktop.ViewModels;

namespace PairSync.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Shows the window again from the tray, the taskbar or a second start.</summary>
    public void Reveal()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || DataContext is not ShellViewModel shell)
            return;

        switch (shell.DecideClose(e.CloseReason))
        {
            case CloseAction.Hide:
                e.Cancel = true;
                Hide();
                break;
            case CloseAction.Minimize:
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                break;
            case CloseAction.Quit:
                // Shutting down closes this window again, then with ApplicationShutdown as the reason.
                e.Cancel = true;
                shell.QuitCommand.Execute(null);
                break;
        }
    }
}
