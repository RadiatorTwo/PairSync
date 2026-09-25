namespace PairSync.Transport;

/// <summary>A reliable, ordered, message-oriented channel inside a session.</summary>
public interface IMessageChannel
{
    string Label { get; }

    bool IsOpen { get; }

    /// <summary>Largest message the remote side accepts, already capped by <see cref="TransportOptions.MaxMessageSize"/>.</summary>
    int MaxMessageSize { get; }

    /// <summary>Bytes queued locally and not yet handed to the network.</summary>
    int BufferedAmount { get; }

    /// <summary>Sends one message; waits (backpressure) while the send queue is above the high watermark.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>Received messages in order. Completes when the channel closes.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken);
}
