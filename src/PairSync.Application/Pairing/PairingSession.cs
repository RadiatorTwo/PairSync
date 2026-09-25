using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Application.Pairing;

/// <summary>Pairing did not complete: connection lost, protocol error, or the invitation was not accepted.</summary>
public class PairingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The other device could not be reached, so nothing of the pairing was sent yet.</summary>
internal sealed class PairingUnreachableException(string message, Exception inner) : PairingException(message, inner);

/// <summary>A user canceled (e.g. "Codes differ"), or nobody confirmed in time. Nothing was stored.</summary>
public sealed class PairingCanceledException(string reason, bool byOtherDevice)
    : PairingException(byOtherDevice ? $"The other device canceled the pairing: {reason}" : $"Pairing canceled: {reason}")
{
    public string Reason { get; } = reason;

    public bool ByOtherDevice { get; } = byOtherDevice;
}

/// <summary>The invitation cannot be used; the message says why, for the user.</summary>
public sealed class InvalidInvitationException(string message, Exception? inner = null) : PairingException(message, inner);

/// <summary>
/// A pairing in progress, from the moment both devices show the security code (plan §5, steps 4–6). Each user
/// confirms or cancels; the other device is stored only once both confirmed. Cancel, timeout or a lost connection
/// store nothing. <see cref="Completion"/> reports the outcome.
/// </summary>
public sealed class PairingSession : IAsyncDisposable
{
    private readonly PeerConnection _connection;
    private readonly Func<PairingSession, Task<PairedDevice>> _store;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _localConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _canceled = new();
    private readonly TimeSpan _confirmationTimeout;
    private string? _cancelReason;
    private int _confirmSent;

    internal PairingSession(
        PeerConnection connection, string securityCode, bool viaInvitation, TimeSpan confirmationTimeout, TimeProvider time,
        Func<PairingSession, Task<PairedDevice>> store, ILogger logger)
    {
        _connection = connection;
        _store = store;
        _logger = logger;
        _confirmationTimeout = confirmationTimeout;
        _timeout = new CancellationTokenSource(confirmationTimeout, time);
        SecurityCode = securityCode;
        ViaInvitation = viaInvitation;
        RemoteName = DeviceNames.Clean(connection.Handshake.RemoteName, connection.Handshake.RemoteDeviceId);
        RemoteFingerprint = DeviceFingerprint.Of(connection.RemotePublicKey);
        Completion = RunAsync();
    }

    public Guid RemoteDeviceId => _connection.Handshake.RemoteDeviceId;

    public string RemoteName { get; }

    public DeviceFingerprint RemoteFingerprint { get; }

    internal byte[] RemotePublicKey => _connection.RemotePublicKey;

    /// <summary>12 digits in groups of four, identical on both devices unless someone is in between.</summary>
    public string SecurityCode { get; }

    /// <summary>The other device started the pairing (found this one on the LAN or redeemed its invitation).</summary>
    public bool IsIncoming => _connection.IsIncoming;

    public bool ViaInvitation { get; }

    /// <summary>
    /// The stored device once both users confirmed. Faults with <see cref="PairingCanceledException"/> (canceled here
    /// or there, or not confirmed in time) or <see cref="PairingException"/> (connection lost).
    /// </summary>
    public Task<PairedDevice> Completion { get; }

    /// <summary>"Codes match — pair". Pairing is complete when the other user confirmed too, see <see cref="Completion"/>.</summary>
    public async Task ConfirmAsync(CancellationToken cancellationToken)
    {
        if (Completion.IsCompleted || _canceled.IsCancellationRequested || Interlocked.Exchange(ref _confirmSent, 1) != 0)
            return;
        try
        {
            await _connection.Channels.Control.SendAsync(new PairConfirm(), cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException)
        {
            return; // Completion reports the lost connection
        }
        _localConfirmed.TrySetResult();
    }

    /// <summary>
    /// "Codes differ — cancel", or the user closed the pairing. Tells the other device and stores nothing. Has no
    /// effect once <see cref="Completion"/> succeeded. If this device already confirmed, the other one may have
    /// completed just before it learns about the cancel; its user then removes this device there.
    /// </summary>
    public async Task CancelAsync(string reason = "the security codes differ")
    {
        Interlocked.CompareExchange(ref _cancelReason, reason, null);
        await _canceled.CancelAsync().ConfigureAwait(false);
        await ((Task)Completion).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task<PairedDevice> RunAsync()
    {
        await Task.Yield(); // the constructor returns before anything happens
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token, _canceled.Token);
        try
        {
            var remote = _connection.Channels.Control.ExpectAsync<PairConfirm>(stop.Token);
            var first = await Task.WhenAny(remote, _localConfirmed.Task).WaitAsync(stop.Token).ConfigureAwait(false);
            await first.ConfigureAwait(false); // a cancel from the other side ends it right away
            await Task.WhenAll(remote, _localConfirmed.Task).WaitAsync(stop.Token).ConfigureAwait(false);

            var device = await _store(this).ConfigureAwait(false);
            _logger.LogInformation("Paired with {Name}, fingerprint {Fingerprint}", device.Name, RemoteFingerprint.ToShortString());
            return device;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            var reason = _canceled.IsCancellationRequested
                ? _cancelReason ?? "canceled"
                : $"the code was not confirmed within {_confirmationTimeout.TotalMinutes:0} minutes";
            await TryTellOtherDeviceAsync(reason).ConfigureAwait(false);
            _logger.LogInformation("Pairing with {Name} canceled: {Reason}", RemoteName, reason);
            throw new PairingCanceledException(reason, byOtherDevice: false);
        }
        catch (TransferCanceledException e)
        {
            _logger.LogInformation("{Name} canceled the pairing: {Reason}", RemoteName, e.Reason);
            throw new PairingCanceledException(e.Reason, byOtherDevice: true);
        }
        catch (Exception e) when (e is TransportException or ProtocolException)
        {
            _logger.LogInformation(e, "Pairing with {Name} failed", RemoteName);
            throw new PairingException($"The connection to {RemoteName} was lost before the pairing was complete.", e);
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task TryTellOtherDeviceAsync(string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _connection.Channels.Control.SendAsync(new Cancel { Reason = reason }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or OperationCanceledException)
        {
            // gone already; it fails on its own
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CancelAsync("the pairing was closed").ConfigureAwait(false);
        _timeout.Dispose();
        _canceled.Dispose();
    }
}
