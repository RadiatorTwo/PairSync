using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

public sealed class NatDiagnosticsTests
{
    private static readonly IPEndPoint PublicA = new(IPAddress.Parse("203.0.113.7"), 61000);
    private static readonly IPEndPoint PublicB = new(IPAddress.Parse("203.0.113.7"), 61001);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NatDiagnostics Create(HostResolver? resolver = null, params IPAddress[] localAddresses) => new(new NatDiagnosticsOptions
    {
        Stun = new StunClientOptions { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(50), Timeout = TimeSpan.FromMilliseconds(400) },
        QuickTimeout = TimeSpan.FromMilliseconds(300),
        DnsTimeout = TimeSpan.FromSeconds(1),
        ResolveHostAsync = resolver ?? ((host, _) => throw new SocketException((int)SocketError.HostNotFound)),
        GetLocalAddresses = () => localAddresses.Length > 0 ? localAddresses : [IPAddress.Parse("192.168.1.20")],
    });

    [Fact]
    public async Task Same_mapping_from_two_servers_is_endpoint_independent()
    {
        using var first = FakeStunServer.Mapping(PublicA);
        using var second = FakeStunServer.Mapping(PublicA);

        var report = await Create().RunAsync([first.Uri, second.Uri], Ct);

        Assert.Equal(NatMappingBehavior.EndpointIndependent, report.Mapping);
        Assert.Equal(NatHint.EndpointIndependent, report.Hint);
        Assert.Equal(PublicA, report.PublicEndPoint);
        Assert.All(report.Servers, s => Assert.Equal(StunServerStatus.Answered, s.Status));
        Assert.False(report.UdpBlocked);
        Assert.False(report.CgnatSuspected);
    }

    [Fact]
    public async Task Different_mapping_per_server_is_symmetric()
    {
        using var first = FakeStunServer.Mapping(PublicA);
        using var second = FakeStunServer.Mapping(PublicB);

        var report = await Create().RunAsync([first.Uri, second.Uri], Ct);

        Assert.Equal(NatMappingBehavior.EndpointDependent, report.Mapping);
        Assert.Equal(NatHint.Symmetric, report.Hint);
    }

    [Fact]
    public async Task Requests_to_all_servers_leave_from_the_same_local_port()
    {
        using var first = FakeStunServer.Start();
        using var second = FakeStunServer.Start();

        var report = await Create().RunAsync([first.Uri, second.Uri], Ct);

        var ports = report.Servers.Select(s => s.Ipv4!.MappedAddress!.Port).Distinct();
        Assert.Single(ports);
    }

    [Fact]
    public async Task Mapped_address_equal_to_local_address_means_no_nat()
    {
        using var first = FakeStunServer.Start();
        using var second = FakeStunServer.Start();

        var report = await Create(null, IPAddress.Loopback).RunAsync([first.Uri, second.Uri], Ct);

        Assert.Equal(NatMappingBehavior.NoNat, report.Mapping);
        Assert.Equal(NatHint.Open, report.Hint);
    }

    [Fact]
    public async Task Single_answer_gives_an_address_but_no_mapping_verdict()
    {
        using var server = FakeStunServer.Mapping(PublicA);

        var report = await Create().RunAsync([server.Uri], Ct);

        Assert.Equal(NatMappingBehavior.Unknown, report.Mapping);
        Assert.Equal(NatHint.Unknown, report.Hint);
        Assert.Equal(PublicA, report.PublicEndPoint);
    }

    [Fact]
    public async Task No_answer_from_any_server_means_udp_blocked()
    {
        using var first = FakeStunServer.Silent();
        using var second = FakeStunServer.Silent();

        var report = await Create().RunAsync([first.Uri, second.Uri], Ct);

        Assert.True(report.UdpBlocked);
        Assert.Equal(NatHint.UdpBlocked, report.Hint);
        Assert.All(report.Servers, s => Assert.Equal(StunServerStatus.NoResponse, s.Status));
        Assert.True(first.Requests > 1, "requests are retransmitted");
    }

    [Fact]
    public async Task One_silent_server_is_reported_without_udp_blocked()
    {
        using var silent = FakeStunServer.Silent();
        using var answering = FakeStunServer.Mapping(PublicA);

        var report = await Create().RunAsync([silent.Uri, answering.Uri], Ct);

        Assert.False(report.UdpBlocked);
        Assert.Equal(StunServerStatus.NoResponse, report.Servers[0].Status);
        Assert.Equal(StunServerStatus.Answered, report.Servers[1].Status);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("100.64.0.1")]
    public async Task Dns_filter_answer_is_reported_as_blocked(string answer)
    {
        using var server = FakeStunServer.Start();
        var diagnostics = Create((_, _) => Task.FromResult(new[] { IPAddress.Parse(answer) }));

        var report = await diagnostics.RunAsync(["stun:stun.example.org:3478"], Ct);

        var check = Assert.Single(report.Servers);
        Assert.Equal(StunServerStatus.DnsBlocked, check.Status);
        Assert.Equal([IPAddress.Parse(answer)], check.ResolvedAddresses);
        Assert.Null(check.Ipv4);
        Assert.True(report.DnsFilterSuspected);
        Assert.False(report.UdpBlocked);
        Assert.Equal(NatHint.Unknown, report.Hint);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Resolved_public_address_is_queried()
    {
        // Resolves to a documentation address nobody answers on; proves the resolver result is used.
        var diagnostics = Create((host, _) => Task.FromResult(new[] { IPAddress.Parse("0.0.0.0"), IPAddress.Parse("192.0.2.1") }));

        var report = await diagnostics.RunAsync(["stun.example.org"], Ct);

        var check = Assert.Single(report.Servers);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 3478), check.Ipv4!.Server);
        Assert.False(report.DnsFilterSuspected);
    }

    [Fact]
    public async Task Unresolvable_and_invalid_entries_are_skipped()
    {
        using var first = FakeStunServer.Mapping(PublicA);
        using var second = FakeStunServer.Mapping(PublicA);

        var report = await Create().RunAsync(["turn:relay.example.org", "stun:nowhere.invalid", first.Uri, second.Uri], Ct);

        Assert.Equal(StunServerStatus.InvalidUri, report.Servers[0].Status);
        Assert.Equal(StunServerStatus.DnsFailed, report.Servers[1].Status);
        Assert.Equal(NatHint.EndpointIndependent, report.Hint);
    }

    [Fact]
    public async Task Name_not_found_while_another_resolves_suggests_a_dns_filter()
    {
        var diagnostics = Create((host, _) => host == "stun.good.example"
            ? Task.FromResult(new[] { IPAddress.Parse("192.0.2.1") })
            : throw new SocketException((int)SocketError.HostNotFound));

        var both = await diagnostics.RunAsync(["stun.good.example", "stun.filtered.example"], Ct);
        var alone = await diagnostics.RunAsync(["stun.filtered.example"], Ct);

        Assert.Equal(StunServerStatus.DnsFailed, both.Servers[1].Status);
        Assert.True(both.DnsFilterSuspected);
        Assert.False(alone.DnsFilterSuspected);
    }

    [Fact]
    public async Task Hanging_dns_is_cut_off()
    {
        var diagnostics = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });

        var report = await diagnostics.RunAsync(["stun.example.org"], Ct);

        Assert.Equal(StunServerStatus.DnsFailed, Assert.Single(report.Servers).Status);
    }

    [Fact]
    public async Task Cgnat_mapped_address_is_flagged()
    {
        using var first = FakeStunServer.Mapping(new IPEndPoint(IPAddress.Parse("100.72.1.2"), 40000));
        using var second = FakeStunServer.Mapping(new IPEndPoint(IPAddress.Parse("100.72.1.2"), 40000));

        var report = await Create().RunAsync([first.Uri, second.Uri], Ct);

        Assert.True(report.CgnatSuspected);
    }

    [Fact]
    public async Task Cgnat_interface_address_is_flagged()
    {
        using var server = FakeStunServer.Mapping(PublicA);

        var report = await Create(null, IPAddress.Parse("100.100.5.6")).RunAsync([server.Uri], Ct);

        Assert.True(report.CgnatSuspected);
    }

    [Fact]
    public async Task Ipv6_is_checked_only_with_a_global_address()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "IPv6 is not available.");
        using var v6 = FakeStunServer.Start(IPAddress.IPv6Loopback);
        using var v4 = FakeStunServer.Mapping(PublicA);

        var without = await Create().RunAsync([v4.Uri, v6.Uri], Ct);
        var with = await Create(null, IPAddress.Parse("192.168.1.20"), IPAddress.Parse("2001:db8::20")).RunAsync([v4.Uri, v6.Uri], Ct);

        Assert.False(without.HasGlobalIpv6Address);
        Assert.False(without.Ipv6Checked);
        Assert.Equal(StunServerStatus.NoUsableAddress, without.Servers[1].Status);
        Assert.True(with.HasGlobalIpv6Address);
        Assert.True(with.Ipv6StunReachable);
        Assert.Equal(IPAddress.IPv6Loopback, with.PublicIpv6EndPoint!.Address);
    }

    [Fact]
    public async Task Quick_hint_is_symmetric_for_different_mappings()
    {
        using var first = FakeStunServer.Mapping(PublicA);
        using var second = FakeStunServer.Mapping(PublicB);

        Assert.Equal(NatHint.Symmetric, await Create().QuickHintAsync([first.Uri, second.Uri], Ct));
    }

    [Fact]
    public async Task Quick_hint_gives_up_after_the_short_timeout()
    {
        using var server = FakeStunServer.Silent();
        var watch = Stopwatch.StartNew();

        var hint = await Create().QuickHintAsync([server.Uri], Ct);

        Assert.Equal(NatHint.UdpBlocked, hint);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1500));
    }

    /// <summary>Needs internet access; prints the report for manual inspection.</summary>
    [Fact(Explicit = true)]
    public async Task Real_servers_produce_a_report()
    {
        var report = await new NatDiagnostics().RunAsync(["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302"], Ct);

        foreach (var server in report.Servers)
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{server.Server}: {server.Status} v4={server.Ipv4?.MappedAddress} v6={server.Ipv6?.MappedAddress} {server.Detail}");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"mapping={report.Mapping} hint={report.Hint} udpBlocked={report.UdpBlocked} dnsFilter={report.DnsFilterSuspected} "
            + $"cgnat={report.CgnatSuspected} ipv6={report.HasGlobalIpv6Address}/{report.Ipv6StunReachable} in {report.Duration}");
        Assert.False(report.UdpBlocked);
        Assert.NotNull(report.PublicEndPoint);
    }

    [Fact]
    public async Task No_servers_gives_unknown()
    {
        var report = await Create().RunAsync([], Ct);

        Assert.Empty(report.Servers);
        Assert.False(report.UdpBlocked);
        Assert.Equal(NatHint.Unknown, report.Hint);
    }
}
