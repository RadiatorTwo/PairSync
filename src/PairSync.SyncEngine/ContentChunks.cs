using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PairSync.SyncEngine;

/// <summary>A content-defined chunk of a file: where it starts, how long it is, its SHA-256.</summary>
public readonly record struct ContentChunk(long Offset, int Length, ReadOnlyMemory<byte> Sha256);

/// <summary>
/// Content-defined chunking (FastCDC with a gear hash): chunk borders depend on the bytes around them, not on their
/// position, so bytes inserted into or removed from a file move the following chunks without changing them. The sync
/// index carries these chunks, and the receiver copies the ones it has from an older version to their new place.
/// Transfers keep their fixed 4 MiB blocks; see <see cref="ChunkSeed"/>.
/// </summary>
/// <remarks>
/// Borders must come out the same on every device and version: the gear table and the masks are part of the protocol.
/// A chunk list is <see cref="EntrySize"/> bytes per chunk: its length (uint32, little endian) and its SHA-256.
/// </remarks>
public static class ContentChunks
{
    public const int MinSize = 512 * 1024;

    /// <summary>Where the stricter mask gives way to the easier one; chunks average a bit above this.</summary>
    public const int NormalSize = 2 * 1024 * 1024;

    public const int MaxSize = 8 * 1024 * 1024;

    public const int EntrySize = 4 + 32;

    // Normalized chunking: 23 bits before the normal size make early borders rare, 19 bits after it make late ones likely.
    private const ulong StrictMask = ~0UL << (64 - 23);
    private const ulong EasyMask = ~0UL << (64 - 19);

    private static readonly ulong[] Gear = CreateGear();

    /// <summary>Length of the chunk at the start of <paramref name="data"/>, which holds <see cref="MaxSize"/> bytes or the rest of the file.</summary>
    public static int NextLength(ReadOnlySpan<byte> data)
    {
        if (data.Length <= MinSize)
            return data.Length;
        var normal = Math.Min(NormalSize, data.Length);
        var end = Math.Min(MaxSize, data.Length);
        ulong hash = 0;
        var i = MinSize;
        for (; i < normal; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & StrictMask) == 0)
                return i + 1;
        }
        for (; i < end; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & EasyMask) == 0)
                return i + 1;
        }
        return end;
    }

    /// <summary>Reads <paramref name="stream"/> to its end; calls <paramref name="onChunk"/> with every chunk in order.</summary>
    /// <returns>The chunk list.</returns>
    public static byte[] Split(Stream stream, Action<ReadOnlySpan<byte>> onChunk, CancellationToken cancellationToken)
    {
        var buffer = new byte[2 * MaxSize];
        var list = new MemoryStream();
        Span<byte> entry = stackalloc byte[EntrySize];
        int start = 0, end = 0;
        var eof = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!eof && end - start < MaxSize)
            {
                // Keep at least one maximal chunk ahead, so a border never depends on how the file was read.
                buffer.AsSpan(start, end - start).CopyTo(buffer);
                end -= start;
                start = 0;
                while (end < buffer.Length)
                {
                    var read = stream.Read(buffer, end, buffer.Length - end);
                    if (read == 0)
                    {
                        eof = true;
                        break;
                    }
                    end += read;
                }
            }
            if (start == end)
                return list.ToArray();
            var chunk = buffer.AsSpan(start, NextLength(buffer.AsSpan(start, Math.Min(end - start, MaxSize))));
            onChunk(chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)chunk.Length);
            SHA256.HashData(chunk, entry[4..]);
            list.Write(entry);
            start += chunk.Length;
        }
    }

    /// <summary>The chunks of a list; null if it is malformed or does not describe a file of <paramref name="fileSize"/> bytes (if given).</summary>
    public static ContentChunk[]? Parse(byte[]? list, long? fileSize = null)
    {
        if (list is null || list.Length % EntrySize != 0)
            return null;
        var chunks = new ContentChunk[list.Length / EntrySize];
        long offset = 0;
        for (var i = 0; i < chunks.Length; i++)
        {
            var at = i * EntrySize;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(at, 4));
            if (length is 0 or > MaxSize)
                return null;
            chunks[i] = new ContentChunk(offset, (int)length, list.AsMemory(at + 4, 32));
            offset += length;
        }
        return fileSize is null || offset == fileSize ? chunks : null;
    }

    /// <summary>256 fixed pseudo-random values (SplitMix64 from a fixed seed), identical everywhere.</summary>
    private static ulong[] CreateGear()
    {
        var gear = new ulong[256];
        var state = 0x5041495253594E43UL; // "PAIRSYNC"
        for (var i = 0; i < gear.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            gear[i] = z ^ (z >> 31);
        }
        return gear;
    }
}
