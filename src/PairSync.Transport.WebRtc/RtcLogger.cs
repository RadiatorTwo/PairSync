using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PairSync.Transport.WebRtc.Native;

namespace PairSync.Transport.WebRtc;

/// <summary>
/// Routes libdatachannel's log output. The level comes from the environment variable
/// PAIRSYNC_RTC_LOG (none, error, warning, info, debug, verbose; default warning).
/// </summary>
public static unsafe class RtcLogger
{
    private static int _initialized;

    /// <summary>Receives native log lines; defaults to standard error.</summary>
    public static Action<string> Sink { get; set; } = line => Console.Error.WriteLine(line);

    internal static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        var level = Environment.GetEnvironmentVariable("PAIRSYNC_RTC_LOG")?.ToLowerInvariant() switch
        {
            "none" => RtcLogLevel.None,
            "error" => RtcLogLevel.Error,
            "info" => RtcLogLevel.Info,
            "debug" => RtcLogLevel.Debug,
            "verbose" => RtcLogLevel.Verbose,
            _ => RtcLogLevel.Warning,
        };
        RtcNative.rtcInitLogger(level, &OnLog);
    }

    private static readonly Lock RepeatLock = new();
    private static string? _lastLine;
    private static int _repeats;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(RtcLogLevel level, byte* message)
    {
        try
        {
            Write($"[rtc {level.ToString().ToLowerInvariant()}] {RtcNative.FromUtf8Z(message)}");
        }
        catch
        {
            // Never let an exception cross back into native code.
        }
    }

    /// <summary>
    /// Collapses runs of identical lines. Tearing down a session while the peer is still sending
    /// produces one "dropping incoming message" warning per packet.
    /// </summary>
    private static void Write(string line)
    {
        string? summary = null;
        lock (RepeatLock)
        {
            if (line == _lastLine)
            {
                _repeats++;
                return;
            }
            if (_repeats > 0)
                summary = $"[rtc] (previous message repeated {_repeats} times)";
            _lastLine = line;
            _repeats = 0;
        }
        if (summary is not null)
            Sink(summary);
        Sink(line);
    }
}
