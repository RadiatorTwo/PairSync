using System.Runtime.InteropServices;

namespace PairSync.Spike;

/// <summary>Writes large incompressible test files quickly (xorshift64*, deterministic per seed).</summary>
public static class TestFileGenerator
{
    public static async Task GenerateAsync(string path, long size, ulong seed, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        const int blockSize = 4 * 1024 * 1024;
        var block = new byte[blockSize];
        var state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 0,
            PreallocationSize = size,
        });

        for (long written = 0; written < size;)
        {
            var words = MemoryMarshal.Cast<byte, ulong>(block.AsSpan());
            for (var i = 0; i < words.Length; i++)
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                words[i] = state * 0x2545F4914F6CDD1DUL;
            }
            var count = (int)Math.Min(blockSize, size - written);
            await stream.WriteAsync(block.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            written += count;
            progress?.Report(written);
        }
    }
}
