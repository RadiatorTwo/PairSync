using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PairSync.Transport.Tls;

/// <summary>
/// Accepts TLS 1.3 connections for the LAN transport. Both sides authenticate with their device certificate;
/// every client key is accepted at the TLS level and exposed as <see cref="ITransportSession.RemotePublicKey"/>,
/// because unknown devices still need a connection to pair. Authorization happens right after the handshake.
/// </summary>
public sealed class TlsListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly SslStreamCertificateContext _certificate;
    private readonly TransportOptions _options;

    private TlsListener(TcpListener listener, X509Certificate2 certificate, TransportOptions options)
    {
        _listener = listener;
        _certificate = SslStreamCertificateContext.Create(certificate, additionalCertificates: null);
        _options = options;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Endpoint = new TlsEndpointInfo(TlsEndpointInfo.LocalAddresses(), Port, TlsEndpointInfo.KeyHash(certificate));
    }

    public int Port { get; }

    public TlsEndpointInfo Endpoint { get; }

    /// <summary>Listens on all interfaces, IPv4 and IPv6.</summary>
    /// <param name="port">TCP port, 0 lets the OS choose.</param>
    /// <param name="certificate">Device certificate with private key; stays owned by the caller.</param>
    /// <exception cref="SocketException">The port is in use or not allowed.</exception>
    public static TlsListener Start(int port, X509Certificate2 certificate, TransportOptions options)
    {
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The certificate needs its private key.", nameof(certificate));
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        return new TlsListener(listener, certificate, options);
    }

    /// <summary>Accepts one connection and completes its handshake.</summary>
    public async Task<ITransportSession> AcceptAsync(IReadOnlyList<string> channelLabels, CancellationToken cancellationToken)
    {
        var socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        return await AuthenticateAsync(socket, channelLabels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts connections until canceled. Handshakes run concurrently, so a slow or hostile client cannot hold up
    /// others. Failed handshakes go to <paramref name="onError"/>; established sessions to <paramref name="onSession"/>,
    /// which owns them.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<string> channelLabels, Func<ITransportSession, Task> onSession, Action<EndPoint?, Exception> onError,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException e)
            {
                onError(null, e);
                continue;
            }

            _ = Task.Run(async () =>
            {
                var remote = socket.RemoteEndPoint;
                ITransportSession session;
                try
                {
                    session = await AuthenticateAsync(socket, channelLabels, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    onError(remote, e);
                    return;
                }
                try
                {
                    await onSession(session).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    onError(remote, e);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }
    }

    private async Task<ITransportSession> AuthenticateAsync(Socket socket, IReadOnlyList<string> channelLabels, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        var stream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificateRequired = true,
                // Self-signed device certificates: trust comes from the pinned key, not from a chain.
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
            }, timeout.Token).ConfigureAwait(false);

            var remoteKey = TlsEndpointInfo.PublicKeyOf(stream.RemoteCertificate)
                ?? throw new AuthenticationException("The client did not present a certificate.");
            return new TlsSession(socket, stream, remoteKey, channelLabels, _options);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            socket.Dispose();
            throw;
        }
    }

    public void Dispose() => _listener.Stop();
}

public static class TlsConnector
{
    /// <summary>Connects to an advertised endpoint and pins the SHA-256 of the listener's public key.</summary>
    public static Task<ITransportSession> ConnectAsync(
        TlsEndpointInfo endpoint, X509Certificate2 clientCertificate, IReadOnlyList<string> channelLabels, TransportOptions options,
        CancellationToken cancellationToken) =>
        ConnectAsync(endpoint.Addresses, endpoint.Port, clientCertificate,
            key => Convert.ToHexStringLower(SHA256.HashData(key)) == endpoint.PublicKeySha256,
            channelLabels, options, cancellationToken);

    /// <summary>
    /// Tries each address in turn and authenticates with <paramref name="clientCertificate"/>. The listener's key
    /// (DER SubjectPublicKeyInfo) must satisfy <paramref name="isExpectedServerKey"/>; pairing passes a check that
    /// accepts any key and verifies it with the security code afterwards.
    /// </summary>
    public static async Task<ITransportSession> ConnectAsync(
        IReadOnlyList<IPAddress> addresses, int port, X509Certificate2 clientCertificate, Func<byte[], bool> isExpectedServerKey,
        IReadOnlyList<string> channelLabels, TransportOptions options, CancellationToken cancellationToken)
    {
        if (!clientCertificate.HasPrivateKey)
            throw new ArgumentException("The certificate needs its private key.", nameof(clientCertificate));
        var clientContext = SslStreamCertificateContext.Create(clientCertificate, additionalCertificates: null);
        var errors = new List<string>();
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{address}: {(e is SocketException s ? s.SocketErrorCode.ToString() : "timeout")}");
                socket.Dispose();
                continue;
            }

            var stream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
            byte[]? serverKey = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ConnectTimeout);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "PairSync",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ClientCertificateContext = clientContext,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        serverKey = TlsEndpointInfo.PublicKeyOf(certificate);
                        return serverKey is not null && isExpectedServerKey(serverKey);
                    },
                }, timeout.Token).ConfigureAwait(false);
                return new TlsSession(socket, stream, serverKey!, channelLabels, options);
            }
            catch (AuthenticationException e)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                socket.Dispose();
                throw new TransportException(
                    $"TLS handshake with {address} failed (not the expected device, or it does not accept this one): {e.Message}", e);
            }
            catch (Exception e) when (e is IOException && !cancellationToken.IsCancellationRequested)
            {
                // The listener closed the connection during the handshake, e.g. because it rejected our certificate.
                await stream.DisposeAsync().ConfigureAwait(false);
                socket.Dispose();
                throw new TransportException($"TLS handshake with {address} was aborted by the other device: {e.Message}", e);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                socket.Dispose();
                throw;
            }
        }
        throw new TransportException($"No address was reachable on port {port}: {string.Join("; ", errors)}");
    }
}
