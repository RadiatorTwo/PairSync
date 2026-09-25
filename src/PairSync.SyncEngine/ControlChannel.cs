using System.Runtime.CompilerServices;
using PairSync.Protocol;
using PairSync.Transport;

namespace PairSync.SyncEngine;

/// <summary>
/// Typed view on the "control" message channel. With <see cref="PeerAccess.PairingOnly"/> it refuses every message an
/// unpaired device may not send: the other side gets a <see cref="Cancel"/> and the reader a <see cref="PeerNotAuthorizedException"/>.
/// </summary>
public sealed class ControlChannel(IMessageChannel channel)
{
    public const string NotPairedReason = "this device is not paired with the other one";

    /// <summary>Set by the handshake; <see cref="PeerAccess.Paired"/> until then so Hello and HelloAck pass.</summary>
    public PeerAccess Access { get; internal set; } = PeerAccess.Paired;

    public ValueTask SendAsync(IControlMessage message, CancellationToken cancellationToken) =>
        channel.SendAsync(ControlCodec.Encode(message, Guid.NewGuid()), cancellationToken);

    public async IAsyncEnumerable<IControlMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var raw in channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = ControlCodec.Decode(raw);
            if (envelope.Message is UnknownControlMessage)
                continue; // sent by a newer minor version; optional by definition
            if (Access == PeerAccess.PairingOnly && !ControlMessageRules.IsAllowedBeforePairing(envelope.Message))
            {
                await TrySendAsync(new Cancel { Reason = NotPairedReason }).ConfigureAwait(false);
                throw new PeerNotAuthorizedException($"{envelope.Message.GetType().Name} refused: {NotPairedReason}.");
            }
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

    private async Task TrySendAsync(IControlMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or OperationCanceledException)
        {
            // the connection is gone anyway
        }
    }
}

public sealed class TransferCanceledException(string reason) : Exception($"Transfer canceled: {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>Raised by the sender's abort switch to simulate a lost connection in resume tests.</summary>
public sealed class SimulatedDisconnectException(int chunks) : Exception($"Simulated disconnect after {chunks} confirmed chunks.");
