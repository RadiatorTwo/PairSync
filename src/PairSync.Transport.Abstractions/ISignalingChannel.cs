namespace PairSync.Transport;

public enum SignalingState
{
    /// <summary>No signaling service configured.</summary>
    Off,
    Connecting,
    Connected,

    /// <summary>Not connected; retries later. The error says why.</summary>
    Failed,
}

/// <summary>
/// Carries signed connection codes between devices that are not on the same LAN and tells which of them are
/// online (plan §6 "Optional später: Rendezvous und Relay"): e.g. a self-hosted rendezvous service. Manual
/// copy-and-paste needs no channel. Devices are addressed by the SHA-256 of their public key.
/// </summary>
public interface ISignalingChannel
{
    SignalingState State { get; }

    event Action? StateChanged;

    /// <summary>A watched device came online (true) or went offline (false).</summary>
    event Action<string, bool>? PresenceChanged;

    /// <summary>Text from the device with this address; the receiver checks it like a pasted code.</summary>
    event Action<string, string>? MessageReceived;

    bool IsOnline(string address);

    /// <summary>Sends <paramref name="data"/> to <paramref name="address"/>; false if not connected to the service.</summary>
    Task<bool> SendAsync(string address, string data, CancellationToken cancellationToken);
}
