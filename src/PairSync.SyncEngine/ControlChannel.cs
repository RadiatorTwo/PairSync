using System.Runtime.CompilerServices;
using PairSync.Protocol;
using PairSync.Transport;

namespace PairSync.SyncEngine;

/// <summary>Typed view on the "control" message channel.</summary>
public sealed class ControlChannel(IMessageChannel channel)
{
    public ValueTask SendAsync(IControlMessage message, CancellationToken cancellationToken) =>
        channel.SendAsync(ControlCodec.Encode(message, Guid.NewGuid()), cancellationToken);

    public async IAsyncEnumerable<IControlMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var raw in channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = ControlCodec.Decode(raw);
            if (envelope.Message is UnknownControlMessage)
                continue; // sent by a newer minor version; optional by definition
            yield return envelope.Message;
        }
    }

    /// <summary>Reads until a message of type <typeparamref name="T"/> arrives; other messages are ignored.</summary>
    public async Task<T> ExpectAsync<T>(CancellationToken cancellationToken) where T : IControlMessage
    {
        await foreach (var message in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (message)
            {
                case T expected:
                    return expected;
                case Cancel cancel:
                    throw new TransferCanceledException(cancel.Reason ?? "canceled by the other device");
            }
        }
        throw new TransportException($"Control channel closed while waiting for {typeof(T).Name}.");
    }
}

public sealed class TransferCanceledException(string reason) : Exception($"Transfer canceled: {reason}");

/// <summary>Raised by the sender's abort switch to simulate a lost connection in resume tests.</summary>
public sealed class SimulatedDisconnectException(int chunks) : Exception($"Simulated disconnect after {chunks} confirmed chunks.");
