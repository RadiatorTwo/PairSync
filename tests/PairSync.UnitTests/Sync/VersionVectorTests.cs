using PairSync.Domain;

namespace PairSync.UnitTests.Sync;

public sealed class VersionVectorTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void Change_on_one_device_makes_its_version_newer()
    {
        var start = VersionVector.Empty.Increment(A);
        var changed = start.Increment(A);

        Assert.Equal(VersionOrder.Newer, changed.Compare(start));
        Assert.Equal(VersionOrder.Older, start.Compare(changed));
        Assert.Equal(VersionOrder.Equal, start.Compare(VersionVector.Parse(start.ToString())));
    }

    [Fact]
    public void Changes_on_both_devices_from_the_same_version_are_concurrent()
    {
        var start = VersionVector.Empty.Increment(A);
        var onA = start.Increment(A);
        var onB = start.Increment(B);

        Assert.Equal(VersionOrder.Concurrent, onA.Compare(onB));
        var merged = onA.Merge(onB);
        Assert.Equal(VersionOrder.Newer, merged.Compare(onA));
        Assert.Equal(VersionOrder.Newer, merged.Compare(onB));
        Assert.Equal(2, merged[A]);
        Assert.Equal(1, merged[B]);
    }

    [Fact]
    public void Text_form_round_trips_and_ignores_zero_counters()
    {
        var vector = VersionVector.From([new(A, 3), new(B, 0)]).Increment(B);

        Assert.Equal($"{A:N}:3,{B:N}:1", vector.ToString());
        Assert.Equal(vector, VersionVector.Parse(vector.ToString()));
        Assert.True(VersionVector.Parse("").IsEmpty);
        Assert.Throws<FormatException>(() => VersionVector.Parse("nonsense"));
    }
}
