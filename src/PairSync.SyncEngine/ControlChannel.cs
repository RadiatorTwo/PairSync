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
                    throw TransferCanceledException.From(cancel, "canceled by the other device");
                case JobControl control when control.Action != JobAction.Resume:
                    throw new JobInterruptedException(control);
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

public class TransferCanceledException(string reason, bool pauseJob = false) : Exception($"Transfer canceled: {reason}")
{
    public string Reason { get; } = reason;

    /// <summary>The receiver kept its progress and asks to pause the job (target disk full).</summary>
    public bool PauseJob { get; } = pauseJob;

    public static TransferCanceledException From(Cancel cancel, string fallbackReason) =>
        new(cancel.Reason ?? fallbackReason, cancel.PauseJob);
}

/// <summary>The target has not enough free space; the temporary file and journal stay for a later resume (plan §12).</summary>
public sealed class TargetFullException(string reason) : TransferCanceledException(reason, pauseJob: true);

/// <summary>The other device paused or canceled the job while this side waited or transferred.</summary>
public sealed class JobInterruptedException(JobControl control)
    : Exception($"The other device {(control.Action == JobAction.Pause ? "paused" : "canceled")} the transfer{(control.Reason is null ? "" : $": {control.Reason}")}.")
{
    public JobControl Control { get; } = control;
}

/// <summary>Raised by the sender's abort switch to simulate a lost connection in resume tests.</summary>
public sealed class SimulatedDisconnectException(int chunks) : Exception($"Simulated disconnect after {chunks} confirmed chunks.");
