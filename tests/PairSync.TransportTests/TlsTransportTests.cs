using System.Net;
using PairSync.Transport;
using PairSync.Transport.Tls;

namespace PairSync.TransportTests;

public sealed class TlsTransportTests
{
    [Fact]
    public async Task Connection_to_an_unexpected_certificate_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = TlsListener.Start(0, LoopbackPair.Options);
        var accept = listener.AcceptAsync(LoopbackPair.Channels, ct);
        var forged = listener.Endpoint with { Addresses = [IPAddress.Loopback], CertificateSha256 = new string('0', 64) };

        await Assert.ThrowsAsync<TransportException>(() => TlsConnector.ConnectAsync(forged, LoopbackPair.Channels, LoopbackPair.Options, ct));

        // With TLS 1.3 the server may finish its side of the handshake before the client rejects the
        // certificate; the session must then end without delivering anything.
        ITransportSession server;
        try
        {
            server = await accept;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        await using (server)
        {
            var control = await server.GetChannelAsync("control", ct);
            var received = 0;
            try
            {
                await foreach (var _ in control.ReadAllAsync(ct).WithCancellation(ct))
                    received++;
            }
            catch (TransportException)
            {
            }
            Assert.Equal(0, received);
            Assert.NotEqual(TransportState.Connected, server.State);
        }
    }

    [Fact]
    public void Connection_code_round_trips()
    {
        var endpoint = new TlsEndpointInfo([IPAddress.Parse("192.168.1.107"), IPAddress.Parse("2001:db8::7")], 47800, new string('a', 64));

        var decoded = TlsEndpointInfo.FromCode("  " + endpoint.ToCode() + "\n");

        Assert.Equal(endpoint.Port, decoded.Port);
        Assert.Equal(endpoint.CertificateSha256, decoded.CertificateSha256);
        Assert.Equal(endpoint.Addresses, decoded.Addresses);
    }

    [Theory]
    [InlineData("PSO1:abc")]
    [InlineData("PST1:")]
    [InlineData("PST1:MHx4fDEuMi4zLjQ")] // "0|x|1.2.3.4": bad port and hash
    public void Malformed_codes_are_rejected(string code) =>
        Assert.ThrowsAny<FormatException>(() => TlsEndpointInfo.FromCode(code));
}
