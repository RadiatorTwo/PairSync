namespace PairSync.SyncEngine;

/// <summary>One bit per chunk: set once the receiver has verified and stored the chunk.</summary>
public sealed class ChunkBitmap
{
    private readonly byte[] _bits;

    public ChunkBitmap(int chunkCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);
        ChunkCount = chunkCount;
        _bits = new byte[(chunkCount + 7) / 8];
    }

    public int ChunkCount { get; }

    public int SetCount { get; private set; }

    public bool IsComplete => SetCount == ChunkCount;

    public bool IsSet(int index)
    {
        CheckIndex(index);
        return (_bits[index >> 3] & (1 << (index & 7))) != 0;
    }

    /// <summary>Sets the bit; returns false if it was already set.</summary>
    public bool Set(int index)
    {
        CheckIndex(index);
        ref var b = ref _bits[index >> 3];
        var mask = (byte)(1 << (index & 7));
        if ((b & mask) != 0)
            return false;
        b |= mask;
        SetCount++;
        return true;
    }

    public byte[] ToBytes() => (byte[])_bits.Clone();

    /// <summary>Restores a bitmap from its wire/journal form; bits beyond <paramref name="chunkCount"/> must be clear.</summary>
    public static ChunkBitmap FromBytes(ReadOnlySpan<byte> bytes, int chunkCount)
    {
        var bitmap = new ChunkBitmap(chunkCount);
        if (bytes.Length != bitmap._bits.Length)
            throw new FormatException($"Bitmap has {bytes.Length} bytes, expected {bitmap._bits.Length}.");
        for (var i = 0; i < chunkCount; i++)
        {
            if ((bytes[i >> 3] & (1 << (i & 7))) != 0)
                bitmap.Set(i);
        }
        for (var i = chunkCount; i < bytes.Length * 8; i++)
        {
            if ((bytes[i >> 3] & (1 << (i & 7))) != 0)
                throw new FormatException("Bitmap has bits set beyond the chunk count.");
        }
        return bitmap;
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)ChunkCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Chunk index must be below {ChunkCount}.");
    }
}
