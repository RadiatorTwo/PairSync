using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PairSync.Application;

namespace PairSync.Desktop;

// Fully qualified: inside PairSync.* the name "Application" means the PairSync.Application namespace.
public sealed partial class App : Avalonia.Application
{
    /// <summary>Null in the designer.</summary>
    public PairSyncCore? Core { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
