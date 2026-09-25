using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

public sealed class StunClientTests
{
    private static readonly StunClientOptions Fast = new()
    {
        InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(50),
        Timeout = TimeSpan.FromSeconds(3),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Binding_returns_the_mapped_address()
    {
        var mapped = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 61000);
        using var server = FakeStunServer.Mapping(mapped);
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.Success, result.Status);
        Assert.Equal(mapped, result.MappedAddress);
        Assert.Equal(server.EndPoint, result.Server);
        Assert.NotNull(result.RoundTripTime);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Binding_works_over_ipv6()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "IPv6 is not available.");
        using var server = FakeStunServer.Start(IPAddress.IPv6Loopback);
        using var client = StunClient.Create(AddressFamily.InterNetworkV6, Fast);

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.Success, result.Status);
        Assert.Equal(IPAddress.IPv6Loopback, result.MappedAddress!.Address);
        Assert.Equal(client.LocalEndPoint!.Port, result.MappedAddress.Port);
    }

    [Fact]
    public async Task Lost_requests_are_retransmitted_until_answered()
    {
        using var server = FakeStunServer.Start();
        var respond = server.Respond;
        server.Respond = (request, source) => server.Requests < 3 ? null : respond(request, source);
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.Success, result.Status);
        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public async Task Silent_server_times_out_after_doubling_retransmissions()
    {
        using var server = FakeStunServer.Silent();
        using var client = StunClient.Create(AddressFamily.InterNetwork, new StunClientOptions
        {
            InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(40),
            Timeout = TimeSpan.FromMilliseconds(600),
        });
        var watch = Stopwatch.StartNew();

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.Timeout, result.Status);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(550), TimeSpan.FromSeconds(3));
        // Sent at 0, 40, 120, 280 ms; the last window (320 ms) is cut to the remaining time.
        Assert.InRange(server.Requests, 3, 5);
    }

    [Fact]
    public async Task Response_with_another_transaction_id_is_ignored()
    {
        using var server = FakeStunServer.Start();
        server.Respond = (_, source) => StunMessage.CreateBindingSuccessResponse(StunMessage.NewTransactionId(), source);
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast with { Timeout = TimeSpan.FromMilliseconds(300) });

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task Error_response_is_reported()
    {
        using var server = FakeStunServer.Start();
        server.Respond = (request, _) =>
        {
            var bytes = StunMessage.CreateBindingRequest(request.TransactionId);
            byte[] response = [.. bytes, 0x00, 0x09, 0x00, 0x04, 0x00, 0x00, 0x04, 0x00];
            response[0] = 0x01;
            response[1] = 0x11;
            response[3] = 8;
            return response;
        };
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.ErrorResponse, result.Status);
        Assert.Equal(400, result.ErrorCode);
        Assert.True(result.Answered);
    }

    [Fact]
    public async Task Response_with_unknown_required_attribute_is_invalid()
    {
        using var server = FakeStunServer.Start();
        server.Respond = (request, source) =>
        {
            byte[] response = [.. StunMessage.CreateBindingSuccessResponse(request.TransactionId, source), 0x7F, 0x01, 0x00, 0x00];
            response[3] += 4;
            return response;
        };
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);

        var result = await client.BindingAsync(server.EndPoint, Ct);

        Assert.Equal(StunBindingStatus.InvalidResponse, result.Status);
        Assert.Null(result.MappedAddress);
    }

    [Fact]
    public async Task Concurrent_requests_on_one_socket_are_matched_by_transaction_id()
    {
        var firstMapped = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 1000);
        var secondMapped = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 2000);
        using var first = FakeStunServer.Mapping(firstMapped);
        using var second = FakeStunServer.Mapping(secondMapped);
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);

        var results = await Task.WhenAll(client.BindingAsync(first.EndPoint, Ct), client.BindingAsync(second.EndPoint, Ct));

        Assert.Equal(firstMapped, results[0].MappedAddress);
        Assert.Equal(secondMapped, results[1].MappedAddress);
    }

    [Fact]
    public async Task Cancellation_stops_the_request()
    {
        using var server = FakeStunServer.Silent();
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.BindingAsync(server.EndPoint, cancel.Token));
    }

    [Fact]
    public async Task Caller_socket_is_used_and_not_disposed()
    {
        using var server = FakeStunServer.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var local = (IPEndPoint)udp.Client.LocalEndPoint!;

        using (var client = new StunClient(udp.Client, Fast))
        {
            var result = await client.BindingAsync(server.EndPoint, Ct);
            Assert.Equal(local, result.MappedAddress);
        }

        udp.Send([1, 2, 3], server.EndPoint); // still open
    }

    [Fact]
    public async Task Address_family_mismatch_is_a_network_error()
    {
        using var client = StunClient.Create(AddressFamily.InterNetwork, Fast);

        var result = await client.BindingAsync(new IPEndPoint(IPAddress.IPv6Loopback, 3478), Ct);

        Assert.Equal(StunBindingStatus.NetworkError, result.Status);
    }

    /// <summary>Needs internet access and a DNS that does not filter STUN servers.</summary>
    [Fact(Explicit = true)]
    public async Task Real_server_reports_a_public_address()
    {
        var addresses = await Dns.GetHostAddressesAsync("stun.cloudflare.com", AddressFamily.InterNetwork, Ct);
        using var client = StunClient.Create(AddressFamily.InterNetwork);

        var result = await client.BindingAsync(new IPEndPoint(addresses[0], 3478), Ct);

        Assert.Equal(StunBindingStatus.Success, result.Status);
        Assert.False(IpAddressRanges.IsDnsFilterAnswer(result.MappedAddress!.Address));
    }
}
