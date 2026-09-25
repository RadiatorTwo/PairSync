using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PairSync.Transport.Tls;

/// <summary>
/// Accepts TLS 1.3 connections for the LAN transport. The certificate is ephemeral for the spike;
/// work package 6 replaces it with the device identity and adds client authentication.
/// </summary>
public sealed class TlsListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly TransportOptions _options;

    private TlsListener(TcpListener listener, X509Certificate2 certificate, TransportOptions options)
    {
        _listener = listener;
        _certificate = certificate;
        _options = options;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Endpoint = new TlsEndpointInfo(TlsEndpointInfo.LocalAddresses(), port, Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)));
    }

    public TlsEndpointInfo Endpoint { get; }

    /// <param name="port">TCP port, 0 lets the OS choose.</param>
    public static TlsListener Start(int port, TransportOptions options)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        return new TlsListener(listener, CreateEphemeralCertificate(), options);
    }

    public async Task<ITransportSession> AcceptAsync(IReadOnlyList<string> channelLabels, CancellationToken cancellationToken)
    {
        var socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        socket.NoDelay = true;
        var stream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(_certificate, additionalCertificates: null),
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, timeout.Token).ConfigureAwait(false);
            return new TlsSession(socket, stream, channelLabels, _options);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _certificate.Dispose();
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PairSync", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));
        // Round-trip through PKCS#12 so SChannel on Windows can use the private key.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }
}

public static class TlsConnector
{
    /// <summary>Tries each advertised address in turn and pins the listener's certificate by its SHA-256.</summary>
    public static async Task<ITransportSession> ConnectAsync(
        TlsEndpointInfo endpoint, IReadOnlyList<string> channelLabels, TransportOptions options, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var address in endpoint.Addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.ConnectAsync(address, endpoint.Port, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{address}: {(e is SocketException s ? s.SocketErrorCode.ToString() : "timeout")}");
                socket.Dispose();
                continue;
            }

            var stream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ConnectTimeout);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "PairSync",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        certificate is not null &&
                        Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())) == endpoint.CertificateSha256,
                }, timeout.Token).ConfigureAwait(false);
                return new TlsSession(socket, stream, channelLabels, options);
            }
            catch (AuthenticationException e)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                socket.Dispose();
                throw new TransportException($"TLS handshake with {address} failed (certificate not the expected one?): {e.Message}", e);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                socket.Dispose();
                throw;
            }
        }
        throw new TransportException($"No advertised address was reachable on port {endpoint.Port}: {string.Join("; ", errors)}");
    }
}
