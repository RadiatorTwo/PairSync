using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PairSync.Stun;

public sealed record StunClientOptions
{
    public static StunClientOptions Default { get; } = new();

    /// <summary>Wait before the first retransmission; doubles after each one (RFC 8489 §6.2.1).</summary>
    public TimeSpan InitialRetransmissionTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Total time for one transaction. With the defaults the request goes out at 0, 0.5, 1.5 and 3.5 s and the client
    /// gives up at 4 s, far shorter than the RFC's 39.5 s: a diagnosis should answer while the user is watching.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(4);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public enum StunBindingStatus
{
    Success,

    /// <summary>No response within <see cref="StunClientOptions.Timeout"/>.</summary>
    Timeout,

    /// <summary>The server answered with a Binding error response, see <see cref="StunBindingResult.ErrorCode"/>.</summary>
    ErrorResponse,

    /// <summary>A matching success response without a mapped address or with unknown comprehension-required attributes.</summary>
    InvalidResponse,

    /// <summary>The request could not be sent (no route, wrong address family, …).</summary>
    NetworkError,
}

public sealed record StunBindingResult(
    StunBindingStatus Status,
    IPEndPoint Server,
    IPEndPoint? MappedAddress = null,
    TimeSpan? RoundTripTime = null,
    int? ErrorCode = null,
    string? Detail = null)
{
    public bool IsSuccess => Status == StunBindingStatus.Success;

    /// <summary>The server sent a matching response of any kind, so UDP to it works.</summary>
    public bool Answered => Status is StunBindingStatus.Success or StunBindingStatus.ErrorResponse or StunBindingStatus.InvalidResponse;
}

/// <summary>
/// Sends STUN Binding requests (RFC 8489) over one UDP socket and matches responses by transaction ID, so several
/// requests to different servers can run concurrently and all see the same local port — which is what the mapping
/// comparison needs. While requests are outstanding, the client reads every datagram arriving on the socket;
/// non-STUN and unmatched datagrams are dropped.
/// </summary>
public sealed class StunClient : IDisposable
{
    /// <summary>Larger than any STUN response we accept; bigger datagrams are dropped.</summary>
    private const int ReceiveBufferSize = 2048;

    // SIO_UDP_CONNRESET: stop Windows from failing the next receive after an ICMP port unreachable.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly Socket _socket;
    private readonly bool _ownsSocket;
    private readonly StunClientOptions _options;
    private readonly ConcurrentDictionary<UInt128, TaskCompletionSource<StunMessage>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task? _receiveLoop;
    private bool _disposed;

    /// <summary>Uses a socket owned by the caller (e.g. <c>UdpClient.Client</c>); it is not disposed with the client.</summary>
    public StunClient(Socket socket, StunClientOptions? options = null)
        : this(socket, ownsSocket: false, options)
    {
    }

    private StunClient(Socket socket, bool ownsSocket, StunClientOptions? options)
    {
        if (socket.SocketType != SocketType.Dgram)
            throw new ArgumentException("STUN needs a UDP socket.", nameof(socket));
        _socket = socket;
        _ownsSocket = ownsSocket;
        _options = options ?? StunClientOptions.Default;
    }

    /// <summary>A client with its own UDP socket on an ephemeral port of all interfaces of <paramref name="family"/>.</summary>
    /// <exception cref="SocketException">The address family is not available.</exception>
    public static StunClient Create(AddressFamily family, StunClientOptions? options = null)
    {
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentOutOfRangeException(nameof(family));
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (family == AddressFamily.InterNetworkV6)
                socket.DualMode = false;
            if (OperatingSystem.IsWindows())
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            return new StunClient(socket, ownsSocket: true, options);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Socket Socket => _socket;

    /// <summary>The bound local endpoint; <c>null</c> for a caller socket that has not sent or been bound yet.</summary>
    public IPEndPoint? LocalEndPoint => _socket.LocalEndPoint as IPEndPoint;

    /// <summary>
    /// Sends a Binding request to <paramref name="server"/>, retransmitting with doubling timeout until a response
    /// arrives or <see cref="StunClientOptions.Timeout"/> elapses. Network problems are reported in the result, not thrown.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<StunBindingResult> BindingAsync(IPEndPoint server, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAdaptEndPoint(server, out var target))
            return new StunBindingResult(StunBindingStatus.NetworkError, server, Detail: "address family does not match the socket");

        var transactionId = StunMessage.NewTransactionId();
        var request = StunMessage.CreateBindingRequest(transactionId);
        var key = Key(transactionId);
        var response = new TaskCompletionSource<StunMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = response;

        var time = _options.TimeProvider;
        var start = time.GetTimestamp();
        var rto = _options.InitialRetransmissionTimeout;
        try
        {
            while (true)
            {
                try
                {
                    await _socket.SendToAsync(request, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    return new StunBindingResult(StunBindingStatus.NetworkError, server, Detail: e.SocketErrorCode.ToString());
                }
                var sentAt = time.GetTimestamp();
                EnsureReceiving();

                var remaining = _options.Timeout - time.GetElapsedTime(start);
                var lastWindow = rto >= remaining;
                try
                {
                    var message = await response.Task
                        .WaitAsync(lastWindow ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : rto, time, cancellationToken)
                        .ConfigureAwait(false);
                    return Evaluate(server, message, time.GetElapsedTime(sentAt));
                }
                catch (TimeoutException)
                {
                }
                if (lastWindow)
                    return new StunBindingResult(StunBindingStatus.Timeout, server);
                rto *= 2;
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        if (_ownsSocket)
            _socket.Dispose();
    }

    private static StunBindingResult Evaluate(IPEndPoint server, StunMessage message, TimeSpan roundTrip)
    {
        if (message.Class == StunMessageClass.ErrorResponse)
            return new StunBindingResult(StunBindingStatus.ErrorResponse, server, RoundTripTime: roundTrip,
                ErrorCode: message.ErrorCode, Detail: message.ErrorReason);
        if (message.UnknownComprehensionRequired.Count > 0)
            return new StunBindingResult(StunBindingStatus.InvalidResponse, server, RoundTripTime: roundTrip,
                Detail: "unknown comprehension-required attribute " + string.Join(", ", message.UnknownComprehensionRequired.Select(t => $"0x{t:X4}")));
        if (message.MappedEndPoint is not { } mapped)
            return new StunBindingResult(StunBindingStatus.InvalidResponse, server, RoundTripTime: roundTrip, Detail: "no mapped address");
        if (mapped.Address.IsIPv4MappedToIPv6)
            mapped = new IPEndPoint(mapped.Address.MapToIPv4(), mapped.Port);
        return new StunBindingResult(StunBindingStatus.Success, server, mapped, roundTrip);
    }

    private bool TryAdaptEndPoint(IPEndPoint server, out IPEndPoint target)
    {
        target = server;
        if (server.AddressFamily == _socket.AddressFamily)
            return true;
        if (_socket.AddressFamily == AddressFamily.InterNetworkV6 && server.AddressFamily == AddressFamily.InterNetwork && _socket.DualMode)
        {
            target = new IPEndPoint(server.Address.MapToIPv6(), server.Port);
            return true;
        }
        if (_socket.AddressFamily == AddressFamily.InterNetwork && server.Address.IsIPv4MappedToIPv6)
        {
            target = new IPEndPoint(server.Address.MapToIPv4(), server.Port);
            return true;
        }
        return false;
    }

    private void EnsureReceiving()
    {
        lock (_gate)
        {
            if (_receiveLoop is null || _receiveLoop.IsCompleted)
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stop.Token));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        EndPoint any = _socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                received = (await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false))
                    .ReceivedBytes;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused
                                                or SocketError.MessageSize)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException
                                          or InvalidOperationException)
            {
                // Stopped, socket closed, or broken; the next request starts a new loop, pending ones time out.
                return;
            }

            if (!StunMessage.TryParse(buffer.AsSpan(0, received), out var message)
                || message.Method != StunMessage.BindingMethod
                || message.Class is not (StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse))
                continue;
            if (_pending.TryGetValue(Key(message.TransactionId), out var pending))
                pending.TrySetResult(message);
        }
    }

    private static UInt128 Key(ReadOnlySpan<byte> transactionId) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(transactionId), BinaryPrimitives.ReadUInt64BigEndian(transactionId[4..]));
}
