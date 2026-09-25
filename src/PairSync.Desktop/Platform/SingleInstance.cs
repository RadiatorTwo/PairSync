using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PairSync.Desktop.Platform;

/// <summary>
/// One running app per data directory. The first start owns a named mutex and listens on a named pipe
/// (a Unix socket in the data directory on Linux); a second start asks it to show its window and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly byte[] ActivateMessage = "activate\n"u8.ToArray();

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Action? _onActivate;
    private bool _pending;
    private Task _listening = Task.CompletedTask;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Becomes the running instance and starts listening; null if another instance already runs.</summary>
    public static SingleInstance? TryAcquire(string dataRoot)
    {
        var mutex = new Mutex(initiallyOwned: false, "PairSync-" + Key(dataRoot));
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous instance crashed; the mutex is ours now
        }
        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        var instance = new SingleInstance(mutex, PipeName(dataRoot));
        instance.StartListening();
        return instance;
    }

    /// <summary>Asks the running instance to show its window. False if it did not answer in time.</summary>
    public static async Task<bool> ActivateRunningAsync(string dataRoot, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName(dataRoot), PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cancel = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cancel.Token).ConfigureAwait(false);
            await client.WriteAsync(ActivateMessage, cancel.Token).ConfigureAwait(false);
            await client.FlushAsync(cancel.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Called on a background thread for each later start. A request before this is set is delivered here.</summary>
    public void SetActivationHandler(Action onActivate)
    {
        bool deliver;
        lock (_gate)
        {
            _onActivate = onActivate;
            deliver = _pending;
            _pending = false;
        }
        if (deliver)
            onActivate();
    }

    private void StartListening()
    {
        if (!OperatingSystem.IsWindows())
        {
            // We own the mutex, so a socket file left here belongs to a crashed instance.
            try
            {
                File.Delete(_pipeName);
            }
            catch (IOException)
            {
            }
        }
        _listening = Task.Run(() => ListenAsync(_stop.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ActivateMessage.Length];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await server.ReadExactlyAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                if (buffer.AsSpan().SequenceEqual(ActivateMessage))
                    Activate();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or EndOfStreamException)
            {
                // a client that sent nothing useful; wait for the next one
            }
            catch (UnauthorizedAccessException)
            {
                return; // cannot create the pipe; later starts simply time out and exit
            }
        }
    }

    private void Activate()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _onActivate;
            _pending = handler is null;
        }
        handler?.Invoke();
    }

    private static string Key(string dataRoot)
    {
        var normalized = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>Windows: a pipe name. Elsewhere an absolute socket path, which .NET uses as is.</summary>
    private static string PipeName(string dataRoot)
    {
        if (OperatingSystem.IsWindows())
            return "PairSync-" + Key(dataRoot);
        var inData = Path.Combine(Path.GetFullPath(dataRoot), "instance.sock");
        // sun_path holds 108 bytes including the terminator.
        return Encoding.UTF8.GetByteCount(inData) < 100 ? inData : Path.Combine(Path.GetTempPath(), "pairsync-" + Key(dataRoot) + ".sock");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listening.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // released from another thread than the owner; process exit frees it
        }
        _mutex.Dispose();
    }
}
