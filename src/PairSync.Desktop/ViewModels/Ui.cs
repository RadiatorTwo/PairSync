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

/// <summary>
/// Merges refresh requests for a page. Core events can come dozens of times per second (every synced file); each
/// refresh reads the database and rebuilds rows, and a page refreshing without pause kept the UI thread busy and, in a
/// VM rendering in software, the whole desktop. So at most one refresh runs, the next starts no sooner than
/// <paramref name="interval"/> after the previous one began, and requests meanwhile become that one refresh.
/// </summary>
internal sealed class RefreshThrottle(Func<Task> refresh, TimeSpan interval)
{
    private readonly Lock _lock = new();
    private bool _scheduled;
    private bool _running;
    private long _lastStart = long.MinValue;

    public void Request()
    {
        TimeSpan delay;
        lock (_lock)
        {
            if (_scheduled)
                return;
            _scheduled = true;
            if (_running)
                return; // started when the running refresh ends
            delay = Delay();
        }
        Start(delay);
    }

    private TimeSpan Delay()
    {
        if (_lastStart == long.MinValue)
            return TimeSpan.Zero;
        var since = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastStart);
        return since >= interval ? TimeSpan.Zero : interval - since;
    }

    private void Start(TimeSpan delay)
    {
        // Background: input and rendering go first.
        if (delay <= TimeSpan.Zero)
            Dispatcher.UIThread.Post(() => _ = RunAsync(), DispatcherPriority.Background);
        else
            Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(() => _ = RunAsync(), delay, DispatcherPriority.Background));
    }

    private async Task RunAsync()
    {
        lock (_lock)
        {
            _scheduled = false;
            _running = true;
            _lastStart = Environment.TickCount64;
        }
        try
        {
            await refresh();
        }
        finally
        {
            TimeSpan? next = null;
            lock (_lock)
            {
                _running = false;
                if (_scheduled)
                    next = Delay();
            }
            if (next is { } delay)
                Start(delay);
        }
    }
}
