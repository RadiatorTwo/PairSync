using Microsoft.Extensions.Logging.Abstractions;
using PairSync.Discovery.Lan;

namespace PairSync.IntegrationTests;

/// <summary>Real mDNS on this machine: two instances in one process see each other over multicast loopback.</summary>
public sealed class LanDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LanDiscovery Create(Guid id, int port, TimeSpan? expiry = null) => new(new LanDiscoveryOptions
    {
        DeviceId = id,
        Port = port,
        ProtocolVersion = "0.2",
        QueryInterval = TimeSpan.FromSeconds(1),
        Expiry = expiry ?? TimeSpan.FromSeconds(30),
        IncludeLoopback = true,
    }, TimeProvider.System, NullLogger<LanDiscovery>.Instance);

    private static Task<LanServiceInfo> SeenAsync(LanDiscovery discovery, Guid id)
    {
        var seen = new TaskCompletionSource<LanServiceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Seen += info =>
        {
            if (info.DeviceId == id)
                seen.TrySetResult(info);
        };
        return seen.Task;
    }

    private static Task LostAsync(LanDiscovery discovery, Guid id)
    {
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Lost += lostId =>
        {
            if (lostId == id)
                lost.TrySetResult();
        };
        return lost.Task;
    }

    [Fact]
    public async Task Instances_find_each_other_and_notice_a_goodbye()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        using var a = Create(idA, 47001);
        var seen = SeenAsync(a, idB);
        var lost = LostAsync(a, idB);
        a.Start();
        Assert.Null(a.Error);

        var b = Create(idB, 47002);
        var bSeesA = SeenAsync(b, idA);
        b.Start();

        var info = await seen.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(47002, info.Port);
        Assert.Equal("0.2", info.ProtocolVersion);
        Assert.NotEmpty(info.Addresses);
        await bSeesA.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.DoesNotContain(a.Current, i => i.DeviceId == idA);

        b.Dispose();
        await lost.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.DoesNotContain(a.Current, i => i.DeviceId == idB);
    }

    [Theory]
    [InlineData(new[] { "id=3f2504e0-4f89-11d3-9a0c-0305e82c3301", "v=0.2" }, true)]
    [InlineData(new[] { "v=0.2", "ID=3f2504e0-4f89-11d3-9a0c-0305e82c3301", "name=ignored" }, true)]
    [InlineData(new[] { "id=not-a-guid", "v=0.2" }, false)]
    [InlineData(new[] { "id=3f2504e0-4f89-11d3-9a0c-0305e82c3301" }, false)]
    [InlineData(new[] { "id=00000000-0000-0000-0000-000000000000", "v=0.2" }, false)]
    [InlineData(new[] { "txtvers=1" }, false)]
    public void Txt_record_needs_id_and_version(string[] strings, bool valid) =>
        Assert.Equal(valid, LanDiscovery.ParseTxt(strings) is not null);
}
