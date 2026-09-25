using PairSync.Protocol;
using PairSync.Spike;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.WebRtc;

namespace PairSync.UnitTests;

public sealed class ChunkBitmapTests
{
    [Fact]
    public void Set_counts_each_chunk_once()
    {
        var bitmap = new ChunkBitmap(10);

        Assert.True(bitmap.Set(3));
        Assert.False(bitmap.Set(3));
        Assert.True(bitmap.Set(9));

        Assert.Equal(2, bitmap.SetCount);
        Assert.True(bitmap.IsSet(3));
        Assert.False(bitmap.IsSet(4));
        Assert.False(bitmap.IsComplete);
    }

    [Fact]
    public void Round_trips_through_bytes()
    {
        var bitmap = new ChunkBitmap(23_552);
        foreach (var i in new[] { 0, 7, 8, 9_780, 23_551 })
            bitmap.Set(i);

        var copy = ChunkBitmap.FromBytes(bitmap.ToBytes(), 23_552);

        Assert.Equal(5, copy.SetCount);
        Assert.True(copy.IsSet(9_780));
        Assert.True(copy.IsSet(23_551));
    }

    [Fact]
    public void Rejects_wrong_length_and_stray_bits()
    {
        Assert.Throws<FormatException>(() => ChunkBitmap.FromBytes(new byte[3], 10));
        Assert.Throws<FormatException>(() => ChunkBitmap.FromBytes([0x00, 0b1000_0000], 10));
    }

    [Fact]
    public void Out_of_range_index_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkBitmap(4).Set(4));
}

public sealed class ReceiverPlanValidationTests
{
    private static readonly ChunkedFileReceiver Receiver = new(Path.GetTempPath(), new InMemoryChunkJournal(), new ReceiverOptions { MaxFileSize = 1L << 40 });

    private static TransferPlan Plan(string? name, long size = 10) => new()
    {
        TransferId = Guid.NewGuid(),
        FileName = name,
        FileSize = size,
        ChunkSize = ProtocolLimits.ChunkSize,
        ChunkCount = (int)Math.Max(1, (size + ProtocolLimits.ChunkSize - 1) / ProtocolLimits.ChunkSize),
    };

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("sub/evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("C:evil")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(" padded ")]
    [InlineData(null)]
    public void Path_traversal_and_absolute_names_are_rejected(string? name) =>
        Assert.Throws<ProtocolException>(() => Receiver.ValidatePlan(Plan(name)));

    [Fact]
    public void Plain_name_is_accepted() =>
        Assert.Equal("Photos-2026.tar", Receiver.ValidatePlan(Plan("Photos-2026.tar")));

    [Fact]
    public void Chunk_count_must_match_the_size() =>
        Assert.Throws<ProtocolException>(() => Receiver.ValidatePlan(Plan("a.bin", 10) with { ChunkCount = 2 }));

    [Fact]
    public void Foreign_chunk_size_is_rejected() =>
        Assert.Throws<ProtocolException>(() => Receiver.ValidatePlan(Plan("a.bin") with { ChunkSize = 1024 }));

    [Fact]
    public void Size_limit_applies() =>
        Assert.Throws<ProtocolException>(() => Receiver.ValidatePlan(Plan("a.bin", (1L << 40) + 1)));
}

public sealed class SignalingCodeTests
{
    [Fact]
    public void Session_description_survives_the_compact_code()
    {
        var sdp = string.Join("\r\n", Enumerable.Range(0, 20).Select(i => $"a=candidate:{i} 1 UDP 2122317823 192.168.1.{i} 5{i:0000} typ host"));

        var code = ManualSignaling.Encode("PSO1:", new SessionDescription(SessionDescriptionType.Offer, sdp));
        var decoded = ManualSignaling.Decode("  " + code + "\n", "PSO1:", SessionDescriptionType.Offer);

        Assert.Equal(sdp, decoded.Sdp);
        Assert.DoesNotContain('+', code);
        Assert.DoesNotContain('/', code);
        Assert.True(code.Length < sdp.Length);
    }

    [Fact]
    public void Wrong_prefix_is_rejected()
    {
        var code = ManualSignaling.Encode("PSA1:", new SessionDescription(SessionDescriptionType.Answer, "v=0"));
        Assert.Throws<FormatException>(() => ManualSignaling.Decode(code, "PSO1:", SessionDescriptionType.Offer));
    }
}

public sealed class CandidateParserTests
{
    [Theory]
    [InlineData("a=candidate:1 1 UDP 2122317823 192.168.1.2 51234 typ host", CandidateType.Host, "192.168.1.2:51234")]
    [InlineData("candidate:2 1 UDP 1686052607 203.0.113.7 40000 typ srflx raddr 192.168.1.2 rport 51234", CandidateType.ServerReflexive, "203.0.113.7:40000")]
    [InlineData("a=candidate:3 1 UDP 41885439 198.51.100.1 3478 typ relay raddr 0.0.0.0 rport 0", CandidateType.Relay, "198.51.100.1:3478")]
    [InlineData("a=candidate:4 1 UDP 2122317823 fe80::1 51234 typ host", CandidateType.Host, "[fe80::1]:51234")]
    [InlineData("garbage", CandidateType.Unknown, "")]
    public void Parses_type_and_endpoint(string line, CandidateType type, string address)
    {
        var parsed = CandidateParser.Parse(line);
        Assert.Equal(type, parsed.Type);
        Assert.Equal(address, parsed.Address);
    }
}

public sealed class SizeParsingTests
{
    [Theory]
    [InlineData("100G", 100L << 30)]
    [InlineData("2GiB", 2L << 30)]
    [InlineData("512m", 512L << 20)]
    [InlineData("1.5K", 1536)]
    [InlineData("12345", 12345)]
    public void Parses_binary_units(string text, long expected) => Assert.Equal(expected, SpikeConsole.ParseSize(text));

    [Theory]
    [InlineData("stun.l.google.com:19302", "stun:stun.l.google.com:19302")]
    [InlineData("stun:stun.example.org:3478", "stun:stun.example.org:3478")]
    [InlineData("turn:user:pw@relay.example.org:3478", "turn:user:pw@relay.example.org:3478")]
    public void Normalizes_ice_server_uris(string input, string expected) =>
        Assert.Equal(expected, SpikeConsole.NormalizeIceServer(input));
}
