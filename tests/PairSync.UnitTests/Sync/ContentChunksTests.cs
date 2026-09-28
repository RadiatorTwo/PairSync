using System.Buffers.Binary;
using System.Security.Cryptography;
using PairSync.SyncEngine;

namespace PairSync.UnitTests.Sync;

public sealed class ContentChunksTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static ContentChunk[] Split(byte[] data)
    {
        var list = ContentChunks.Split(new MemoryStream(data), _ => { }, CancellationToken.None);
        return ContentChunks.Parse(list, data.Length)!;
    }

    private static HashSet<string> Hashes(IEnumerable<ContentChunk> chunks) => [.. chunks.Select(c => Convert.ToHexString(c.Sha256.Span))];

    [Fact]
    public void Chunks_stay_within_the_limits_and_cover_the_file()
    {
        var data = RandomBytes(40 * 1024 * 1024, 1);

        var chunks = Split(data);

        Assert.All(chunks[..^1], c => Assert.InRange(c.Length, ContentChunks.MinSize, ContentChunks.MaxSize));
        Assert.Equal(data.Length, chunks.Sum(c => (long)c.Length));
        // Borders come from the content: sizes vary, and on average stay near the normal size.
        Assert.True(chunks.Select(c => c.Length).Distinct().Count() > 1);
        Assert.InRange(data.Length / chunks.Length, ContentChunks.NormalSize / 2, ContentChunks.NormalSize * 2);
    }

    [Fact]
    public void Inserted_bytes_change_only_the_chunks_around_them()
    {
        var data = RandomBytes(32 * 1024 * 1024, 2);
        var changed = data[..5_000_000].Concat(RandomBytes(1000, 3)).Concat(data[5_000_000..]).ToArray();

        var before = Hashes(Split(data));
        var after = Split(changed);

        var fresh = after.Where(c => !before.Contains(Convert.ToHexString(c.Sha256.Span))).ToList();
        Assert.InRange(fresh.Count, 1, 2);
        Assert.InRange(fresh.Sum(c => (long)c.Length), 1, 2 * ContentChunks.MaxSize);
    }

    [Fact]
    public void Borders_do_not_depend_on_how_the_stream_is_read()
    {
        var data = RandomBytes(20 * 1024 * 1024, 4);

        var whole = ContentChunks.Split(new MemoryStream(data), _ => { }, CancellationToken.None);
        var trickled = ContentChunks.Split(new TrickleStream(data), _ => { }, CancellationToken.None);

        Assert.Equal(whole, trickled);
    }

    [Fact]
    public void Borders_are_the_same_on_every_device()
    {
        // Pinned: a change to the gear table or the masks would break chunk reuse between versions.
        var data = new byte[16 * 1024 * 1024];
        var state = 1UL;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            data[i] = (byte)(state >> 56);
        }
        var chunks = Split(data);

        // Worked out independently (Python reimplementation of the same gear table and masks).
        Assert.Equal([2315330, 1898675, 2164701, 2982338, 3214415, 2341258, 1860499], chunks.Select(c => c.Length));
    }

    [Fact]
    public void Small_and_empty_files_are_one_chunk_or_none()
    {
        Assert.Empty(Split([]));
        var small = Assert.Single(Split([1, 2, 3]));
        Assert.Equal(SHA256.HashData([1, 2, 3]), small.Sha256.ToArray());
    }

    [Fact]
    public void Malformed_lists_are_rejected()
    {
        var list = new byte[ContentChunks.EntrySize];
        BinaryPrimitives.WriteUInt32LittleEndian(list, 10);

        Assert.NotNull(ContentChunks.Parse(list, 10));
        Assert.Null(ContentChunks.Parse(list, 11));
        Assert.Null(ContentChunks.Parse(list[..^1], 10));
        BinaryPrimitives.WriteUInt32LittleEndian(list, 0);
        Assert.Null(ContentChunks.Parse(list, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(list, ContentChunks.MaxSize + 1);
        Assert.Null(ContentChunks.Parse(list));
    }

    /// <summary>Hands out a few bytes per read, like a slow disk or network share.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        private int _next = 1;

        public override int Read(byte[] buffer, int offset, int count)
        {
            _next = _next * 7 % 65521;
            return base.Read(buffer, offset, Math.Min(count, _next));
        }
    }
}
