using PairSync.Protocol;
using PairSync.Transport;

namespace PairSync.SyncEngine;

/// <summary>This device as it introduces itself in <see cref="Hello"/>.</summary>
public sealed record LocalDevice(Guid DeviceId, string Name);

/// <summary>The answering side's verdict on a <see cref="Hello"/>: an access level, or a reason to refuse.</summary>
public sealed record AccessDecision(PeerAccess? Access, string? RejectReason)
{
    public static AccessDecision Grant(PeerAccess access) => new(access, null);

    public static AccessDecision Reject(string reason) => new(null, reason);
}

/// <summary>Channels of a session after the handshake, with the negotiated message size and the access level.</summary>
public sealed record PeerChannels(ControlChannel Control, IMessageChannel Data, int MaxMessageSize, PeerAccess Access);

/// <summary>Result of the handshake: who the other device says it is and what it may do.</summary>
public sealed record PeerHandshake(PeerChannels Channels, Guid RemoteDeviceId, string RemoteName, ushort RemoteMinor)
{
    public PeerAccess Access => Channels.Access;
}

/// <summary>
/// Hello/HelloAck at the start of every session. The connecting side sends <see cref="Hello"/>; the answering side
/// checks it (key, id, trust) and grants an access level or refuses with <see cref="Cancel"/>. The connecting side
/// checks the answer the same way; the session is <see cref="PeerAccess.Paired"/> only if both sides say so.
/// In <see cref="PeerAccess.PairingOnly"/> sessions the control channel rejects everything except pairing
/// messages, and any data message closes the session.
/// </summary>
public static class SessionHandshake
{
    public static readonly string[] ChannelLabels = ["control", "data"];

    /// <summary>A device that connects but does not introduce itself in time is dropped.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);

    /// <param name="authorize">Decides on the answering device; sees its <see cref="HelloAck"/> (the key is on the session).</param>
    /// <param name="forPairing">Asks for a pairing session (<see cref="Hello.Pairing"/>); the result is then never <see cref="PeerAccess.Paired"/>.</param>
    /// <exception cref="PeerRejectedException">One of the two devices refused the connection.</exception>
    public static async Task<PeerHandshake> InitiateAsync(
        ITransportSession session, LocalDevice local, Func<HelloAck, ValueTask<AccessDecision>> authorize, CancellationToken cancellationToken,
        bool forPairing = false)
    {
        var (control, data) = await OpenChannelsAsync(session, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        await control.SendAsync(new Hello
        {
            DeviceId = local.DeviceId,
            DeviceName = local.Name,
            MaxMessageSize = data.MaxMessageSize,
            ProtocolMinor = ProtocolVersion.Minor,
            Pairing = forPairing,
        }, timeout.Token).ConfigureAwait(false);

        HelloAck ack;
        try
        {
            ack = await control.ExpectAsync<HelloAck>(timeout.Token).ConfigureAwait(false);
        }
        catch (TransferCanceledException e)
        {
            throw new PeerRejectedException(e.Reason, byOtherDevice: true);
        }

        var decision = await authorize(ack).ConfigureAwait(false);
        if (decision.Access is not { } granted)
        {
            var reason = decision.RejectReason ?? "not allowed";
            await TryRefuseAsync(control, reason, timeout.Token).ConfigureAwait(false);
            throw new PeerRejectedException(reason, byOtherDevice: false);
        }

        var access = granted == PeerAccess.Paired && ack.Access == PeerAccess.Paired && !forPairing ? PeerAccess.Paired : PeerAccess.PairingOnly;
        return Complete(session, control, data, access, ack.DeviceId, ack.DeviceName, ack.MaxMessageSize, ack.ProtocolMinor);
    }

    /// <param name="authorize">Decides on the connecting device; sees its <see cref="Hello"/> (the key is on the session).</param>
    /// <exception cref="PeerRejectedException">This side refused the device; the other side was told why.</exception>
    public static async Task<PeerHandshake> RespondAsync(
        ITransportSession session, LocalDevice local, Func<Hello, ValueTask<AccessDecision>> authorize, CancellationToken cancellationToken)
    {
        var (control, data) = await OpenChannelsAsync(session, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var hello = await control.ExpectAsync<Hello>(timeout.Token).ConfigureAwait(false);
        var decision = await authorize(hello).ConfigureAwait(false);
        if (decision.Access is not { } access)
        {
            var reason = decision.RejectReason ?? "not allowed";
            await TryRefuseAsync(control, reason, timeout.Token).ConfigureAwait(false);
            throw new PeerRejectedException(reason, byOtherDevice: false);
        }

        await control.SendAsync(new HelloAck
        {
            DeviceId = local.DeviceId,
            DeviceName = local.Name,
            MaxMessageSize = data.MaxMessageSize,
            ProtocolMinor = ProtocolVersion.Minor,
            Access = access,
        }, timeout.Token).ConfigureAwait(false);

        return Complete(session, control, data, access, hello.DeviceId, hello.DeviceName, hello.MaxMessageSize, hello.ProtocolMinor);
    }

    /// <summary>Tells the other side why; it may have hung up already, which changes nothing.</summary>
    private static async Task TryRefuseAsync(ControlChannel control, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await control.SendAsync(new Cancel { Reason = reason }, cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException)
        {
        }
    }

    private static async Task<(ControlChannel Control, IMessageChannel Data)> OpenChannelsAsync(
        ITransportSession session, CancellationToken cancellationToken)
    {
        var control = await session.GetChannelAsync("control", cancellationToken).ConfigureAwait(false);
        var data = await session.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);
        return (new ControlChannel(control), data);
    }

    private static PeerHandshake Complete(
        ITransportSession session, ControlChannel control, IMessageChannel data, PeerAccess access,
        Guid remoteId, string? remoteName, int remoteMaxMessage, ushort remoteMinor)
    {
        control.Access = access;
        if (access == PeerAccess.PairingOnly)
            _ = CloseOnDataAsync(session, data);
        var maxMessage = Math.Min(data.MaxMessageSize, remoteMaxMessage > 0 ? remoteMaxMessage : data.MaxMessageSize);
        return new PeerHandshake(new PeerChannels(control, data, maxMessage, access), remoteId, remoteName ?? "", remoteMinor);
    }

    /// <summary>Unpaired devices must not send file data; the first data message ends the session.</summary>
    private static async Task CloseOnDataAsync(ITransportSession session, IMessageChannel data)
    {
        try
        {
            await foreach (var _ in data.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (TransportException)
        {
            // session already gone
        }
    }
}
