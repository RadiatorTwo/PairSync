namespace PairSync.SyncEngine;

/// <summary>
/// Token bucket for the upload limit (plan §8, settings). Shared by all senders, so the limit applies to the device,
/// not per transfer. The rate is read on every call, so a changed setting takes effect immediately; 0 = unlimited.
/// The bucket holds at most one second of tokens, which keeps bursts short.
/// </summary>
public sealed class UploadThrottle(Func<long> bytesPerSecond, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private double _tokens;
    private long _lastTimestamp = time.GetTimestamp();
    private long _lastRate;

    public static UploadThrottle Unlimited { get; } = new(() => 0, TimeProvider.System);

    /// <summary>Waits until <paramref name="bytes"/> may be sent.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytesPerSecond() <= 0)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var rate = bytesPerSecond();
                if (rate <= 0)
                    return;
                Refill(rate);
                // A message larger than the bucket may go once the bucket is full, else it would wait forever.
                var needed = Math.Min(bytes, rate);
                if (_tokens >= needed)
                {
                    _tokens -= bytes;
                    return;
                }
                var wait = TimeSpan.FromSeconds((needed - _tokens) / rate);
                await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Refill(long rate)
    {
        var now = time.GetTimestamp();
        if (rate != _lastRate)
        {
            // New limit: start with an empty bucket so a lowered limit applies right away.
            _lastRate = rate;
            _tokens = Math.Min(_tokens, 0);
        }
        else
        {
            _tokens = Math.Min(rate, _tokens + time.GetElapsedTime(_lastTimestamp, now).TotalSeconds * rate);
        }
        _lastTimestamp = now;
    }
}
