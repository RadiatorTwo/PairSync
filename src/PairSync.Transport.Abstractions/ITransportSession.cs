namespace PairSync.Transport;

public enum TransportState { New, Connecting, Connected, Disconnected, Failed, Closed }

/// <summary>An encrypted peer-to-peer session carrying several named message channels.</summary>
public interface ITransportSession : IAsyncDisposable
{
    TransportState State { get; }

    /// <summary>The selected candidate pair once connected, otherwise null.</summary>
    RouteInfo? Route { get; }

    /// <summary>
    /// DER SubjectPublicKeyInfo of the certificate the other side authenticated with; null if the transport
    /// does not bind the session to a device key (yet).
    /// </summary>
    byte[]? RemotePublicKey { get; }

    /// <summary>Raised when the session leaves the connected state for good (disconnected, failed or closed).</summary>
    event EventHandler<TransportState>? StateChanged;

    /// <summary>Only for the offering side: applies the remote answer.</summary>
    Task ApplyAnswerAsync(SessionDescription answer, CancellationToken cancellationToken);

    /// <summary>Waits until the channel with this label is open on both sides.</summary>
    Task<IMessageChannel> GetChannelAsync(string label, CancellationToken cancellationToken);
}
