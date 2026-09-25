using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PairSync.Transport.WebRtc.Native;

// Subset of libdatachannel's C API (include/rtc/rtc.h, v0.24). Callbacks use the default cdecl
// convention (the library is built without CAPI_STDCALL).

internal enum RtcState { New = 0, Connecting = 1, Connected = 2, Disconnected = 3, Failed = 4, Closed = 5 }

internal enum RtcGatheringState { New = 0, InProgress = 1, Complete = 2 }

internal enum RtcLogLevel { None = 0, Fatal = 1, Error = 2, Warning = 3, Info = 4, Debug = 5, Verbose = 6 }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RtcConfiguration
{
    public byte** IceServers;
    public int IceServersCount;
    public byte* ProxyServer;
    public byte* BindAddress;
    public int CertificateType;
    public int IceTransportPolicy;
    public byte EnableIceTcp;
    public byte EnableIceUdpMux;
    public byte DisableAutoNegotiation;
    public byte ForceMediaTransport;
    public ushort PortRangeBegin;
    public ushort PortRangeEnd;
    public int Mtu;
    public int MaxMessageSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RtcReliability
{
    public byte Unordered;
    public byte Unreliable;
    public uint MaxPacketLifeTime;
    public uint MaxRetransmits;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RtcDataChannelInit
{
    public RtcReliability Reliability;
    public byte* Protocol;
    public byte Negotiated;
    public byte ManualStream;
    public ushort Stream;
}

/// <summary>Process-wide SCTP tuning; values &lt;= 0 keep libdatachannel's defaults. Applies to new PeerConnections.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RtcSctpSettings
{
    public int RecvBufferSize;
    public int SendBufferSize;
    public int MaxChunksOnQueue;
    public int InitialCongestionWindow;
    public int MaxBurst;
    public int CongestionControlModule;
    public int DelayedSackTimeMs;
    public int MinRetransmitTimeoutMs;
    public int MaxRetransmitTimeoutMs;
    public int InitialRetransmitTimeoutMs;
    public int MaxRetransmitAttempts;
    public int HeartbeatIntervalMs;
}

internal static unsafe partial class RtcNative
{
    public const string Library = "datachannel";

    public const int ErrInvalid = -1;
    public const int ErrFailure = -2;
    public const int ErrNotAvailable = -3;
    public const int ErrTooSmall = -4;

    static RtcNative() => NativeLibraryLoader.EnsureRegistered();

    [LibraryImport(Library)]
    public static partial void rtcInitLogger(RtcLogLevel level, delegate* unmanaged[Cdecl]<RtcLogLevel, byte*, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetSctpSettings(RtcSctpSettings* settings);

    [LibraryImport(Library)]
    public static partial void rtcSetUserPointer(int id, nint ptr);

    [LibraryImport(Library)]
    public static partial int rtcCreatePeerConnection(RtcConfiguration* config);

    [LibraryImport(Library)]
    public static partial int rtcDeletePeerConnection(int pc);

    [LibraryImport(Library)]
    public static partial int rtcSetStateChangeCallback(int pc, delegate* unmanaged[Cdecl]<int, RtcState, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetGatheringStateChangeCallback(int pc, delegate* unmanaged[Cdecl]<int, RtcGatheringState, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetDataChannelCallback(int pc, delegate* unmanaged[Cdecl]<int, int, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetRemoteDescription(int pc, byte* sdp, byte* type);

    [LibraryImport(Library)]
    public static partial int rtcGetLocalDescription(int pc, byte* buffer, int size);

    [LibraryImport(Library)]
    public static partial int rtcGetSelectedCandidatePair(int pc, byte* local, int localSize, byte* remote, int remoteSize);

    [LibraryImport(Library)]
    public static partial int rtcCreateDataChannelEx(int pc, byte* label, RtcDataChannelInit* init);

    [LibraryImport(Library)]
    public static partial int rtcGetDataChannelLabel(int dc, byte* buffer, int size);

    [LibraryImport(Library)]
    public static partial int rtcSetOpenCallback(int id, delegate* unmanaged[Cdecl]<int, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetClosedCallback(int id, delegate* unmanaged[Cdecl]<int, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetErrorCallback(int id, delegate* unmanaged[Cdecl]<int, byte*, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSetMessageCallback(int id, delegate* unmanaged[Cdecl]<int, byte*, int, nint, void> cb);

    [LibraryImport(Library)]
    public static partial int rtcSendMessage(int id, byte* data, int size);

    [LibraryImport(Library)]
    public static partial int rtcDelete(int id);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool rtcIsOpen(int id);

    [LibraryImport(Library)]
    public static partial int rtcMaxMessageSize(int id);

    [LibraryImport(Library)]
    public static partial int rtcGetBufferedAmount(int id);

    [LibraryImport(Library)]
    public static partial int rtcSetBufferedAmountLowThreshold(int id, int amount);

    [LibraryImport(Library)]
    public static partial int rtcSetBufferedAmountLowCallback(int id, delegate* unmanaged[Cdecl]<int, nint, void> cb);

    /// <summary>Throws for negative (error) return codes, otherwise passes the value through.</summary>
    public static int Check(int result, [CallerArgumentExpression(nameof(result))] string? call = null) =>
        result >= 0 ? result : throw new TransportException($"libdatachannel call failed ({Describe(result)}): {call}");

    public static string Describe(int error) => error switch
    {
        ErrInvalid => "invalid argument",
        ErrFailure => "runtime error",
        ErrNotAvailable => "not available",
        ErrTooSmall => "buffer too small",
        _ => $"error {error}",
    };

    public delegate int StringGetter(byte* buffer, int size);

    /// <summary>Calls a "fill this char buffer" function, growing the buffer while it is too small.</summary>
    public static string? GetString(StringGetter getter)
    {
        for (var size = 4096; size <= 1 << 22; size *= 4)
        {
            var buffer = new byte[size];
            int result;
            fixed (byte* p = buffer)
                result = getter(p, size);
            if (result == ErrTooSmall)
                continue;
            if (result < 0)
                return null;
            var length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? size : length);
        }
        return null;
    }

    /// <summary>Null-terminated UTF-8 copy of a string for passing to native code.</summary>
    public static byte[] Utf8Z(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    public static string? FromUtf8Z(byte* value) =>
        value == null ? null : Marshal.PtrToStringUTF8((nint)value);
}
