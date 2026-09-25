namespace PairSync.Transport;

/// <summary>Settings for establishing a transport session.</summary>
public sealed record TransportOptions
{
    /// <summary>ICE servers as URIs, e.g. <c>stun:stun.example.org:3478</c>. Empty means LAN only (host candidates).</summary>
    public IReadOnlyList<string> IceServers { get; init; } = [];

    /// <summary>Local UDP port range for ICE; 0/0 lets the stack choose.</summary>
    public ushort PortRangeBegin { get; init; }
    public ushort PortRangeEnd { get; init; }

    /// <summary>Send pauses while more than this many bytes are queued on a channel.</summary>
    public int SendHighWatermark { get; init; } = 4 * 1024 * 1024;

    /// <summary>Paused sends resume once the queue drains below this many bytes.</summary>
    public int SendLowThreshold { get; init; } = 1024 * 1024;

    /// <summary>Upper bound for a single data channel message we are willing to send or accept.</summary>
    public int MaxMessageSize { get; init; } = 256 * 1024;

    /// <summary>
    /// SCTP send/receive buffer per association. It caps the bytes in flight, so throughput is at most
    /// buffer / RTT (1 MiB at 40 ms ≈ 25 MiB/s). 0 keeps the library default (1 MiB).
    /// </summary>
    public int SctpBufferSize { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Path MTU assumed for SCTP packets; 0 uses the library default (1280, the IPv6 minimum).
    /// Larger values mean fewer packets per chunk but risk fragmentation on some paths.
    /// </summary>
    public int Mtu { get; init; }

    /// <summary>
    /// How long to wait for the connection once both sides have each other's description: for TLS the whole
    /// connect, for WebRTC the offering side's wait after applying the answer.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// WebRTC: how long to collect ICE candidates for a description. If STUN servers do not answer in time,
    /// the description goes out with the candidates found so far.
    /// </summary>
    public TimeSpan GatheringTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// WebRTC: how long the answering side waits for the connection after creating its answer. The answer
    /// travels back by hand, so this covers the time until the other side applies it.
    /// </summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>WebRTC: how long a connected session may stay disconnected before it counts as failed.</summary>
    public TimeSpan DisconnectGracePeriod { get; init; } = TimeSpan.FromSeconds(15);
}
