using System.Net;
using System.Net.Sockets;
using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

/// <summary>A STUN server on IPv4 or IPv6 loopback whose answers the test controls.</summary>
internal sealed class FakeStunServer : IDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private int _requests;

    private FakeStunServer(IPAddress loopback)
    {
        _socket = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(loopback, 0));
        EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _ = Task.Run(RunAsync);
    }

    public IPEndPoint EndPoint { get; }

    public string Uri => EndPoint.AddressFamily == AddressFamily.InterNetworkV6
        ? $"stun:[{EndPoint.Address}]:{EndPoint.Port}"
        : $"stun:{EndPoint.Address}:{EndPoint.Port}";

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Returns the response to send for a request from the given source, or <c>null</c> to stay silent.</summary>
    public Func<StunMessage, IPEndPoint, byte[]?> Respond { get; set; } =
        (request, source) => StunMessage.CreateBindingSuccessResponse(request.TransactionId, source);

    public static FakeStunServer Start(IPAddress? loopback = null) => new(loopback ?? IPAddress.Loopback);

    /// <summary>Answers every request with the given mapped address.</summary>
    public static FakeStunServer Mapping(IPEndPoint mapped) => new(IPAddress.Loopback)
    {
        Respond = (request, _) => StunMessage.CreateBindingSuccessResponse(request.TransactionId, mapped),
    };

    public static FakeStunServer Silent() => new(IPAddress.Loopback) { Respond = (_, _) => null };

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
    }

    private async Task RunAsync()
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(EndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (!StunMessage.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var request)
                || request.Class != StunMessageClass.Request)
                continue;
            Interlocked.Increment(ref _requests);
            var source = (IPEndPoint)received.RemoteEndPoint;
            if (Respond(request, source) is { } response)
            {
                try
                {
                    await _socket.SendToAsync(response, SocketFlags.None, source);
                }
                catch (Exception e) when (e is ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }
    }
}
