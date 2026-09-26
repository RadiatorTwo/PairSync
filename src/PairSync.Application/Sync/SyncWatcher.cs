namespace PairSync.Application.Sync;

/// <summary>
/// Watches a profile folder and asks for a scan once changes have settled (phase 3 block B). File system events can
/// be lost (buffer overflow, network drives, inotify limits), so the periodic rescan stays the safety net; a watcher
/// that fails only reports it and keeps asking for scans on its timer.
/// </summary>
public sealed class SyncWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly ITimer _quiet;
    private readonly ITimer _rescan;
    private readonly TimeSpan _settle;
    private readonly Action _scan;
    private int _disposed;

    /// <param name="scan">Called after changes settled and on every rescan interval; must not block.</param>
    public SyncWatcher(string root, TimeSpan settle, TimeSpan rescanInterval, TimeProvider time, Action scan, Action<string>? problem = null)
    {
        _settle = settle;
        _scan = scan;
        _quiet = time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _rescan = time.CreateTimer(_ => Fire(), null, rescanInterval, rescanInterval);
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, e) =>
            {
                // Overflow: events were lost, a full scan finds them.
                problem?.Invoke($"The folder watcher lost events ({e.GetException().Message}); rescanning.");
                Changed();
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _watcher?.Dispose();
            _watcher = null;
            problem?.Invoke($"Changes are found by the periodic rescan only: the folder cannot be watched ({e.Message}).");
        }
    }

    public bool IsWatching => _watcher is not null;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var path = SyncPaths.Normalize(e.Name ?? "");
        if (ExcludeRules.IsAlwaysExcluded(path))
            return;
        Changed();
    }

    /// <summary>Restarts the quiet period; the scan runs when nothing changed for the settle time.</summary>
    public void Changed()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _quiet.Change(_settle, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _scan();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _watcher?.Dispose();
        _quiet.Dispose();
        _rescan.Dispose();
    }
}
