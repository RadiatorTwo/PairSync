using Avalonia.Threading;

namespace PairSync.Desktop.ViewModels;

/// <summary>Core services raise their events on background threads; view models change on the UI thread.</summary>
internal static class Ui
{
    public static void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
