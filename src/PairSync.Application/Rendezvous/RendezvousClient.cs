using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using PairSync.Application.Internet;
using PairSync.Application.Presence;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Protocol.Rendezvous;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using PairSync.Transport;

namespace PairSync.Application.Rendezvous;

public sealed record RendezvousClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits between reconnect attempts; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
}

/// <summary>What Settings shows about the rendezvous service.</summary>
public sealed record RendezvousStatus(SignalingState State, string? Url, string? Error, DateTime? NextAttemptUtc);

/// <summary>
/// Connection to the optional rendezvous service (phase 5 block C): logs in with the device key, watches the
/// paired, not blocked devices and relays their connection codes. Keeps reconnecting while a URL is set; without
/// one it makes no request at all. Relays handed out by the service go to <see cref="RelayServers"/>.
/// </summary>
public sealed class RendezvousClient : ISignalingChannel, IAsyncDisposable
{
    private readonly CurrentIdentity _identity;
    private readonly SettingsStore _settings;
    private readonly PresenceService _presence;
    private readonly RelayServers _relays;
    private readonly RendezvousClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RendezvousClient> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _online = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private ClientWebSocket? _socket;
    private string[] _watched = [];
    private string? _url;
    private RendezvousStatus _status = new(SignalingState.Off, null, null, null);
    private long _nextId;
    private int _disposed;

    public RendezvousClient(
        CurrentIdentity identity, SettingsStore settings, PresenceService presence, RelayServers relays, RendezvousClientOptions options,
        TimeProvider time, ILogger<RendezvousClient> logger)
    {
        _identity = identity;
        _settings = settings;
        _presence = presence;
        _relays = relays;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public RendezvousStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public SignalingState State => Status.State;

    /// <summary>This device's address at the service.</summary>
    public string OwnAddress => RendezvousAuth.AddressOf(_identity.Value.Identity.PublicKey);

    public event Action? StateChanged;

    public event Action<string, bool>? PresenceChanged;

    public event Action<string, string>? MessageReceived;

    /// <summary>The address of <paramref name="device"/> at the service.</summary>
    public static string AddressOf(PairedDevice device) => RendezvousAuth.AddressOf(device.PublicKey);

    public bool IsOnline(string address)
    {
        lock (_gate)
            return _online.Contains(address);
    }

    /// <summary>Follows the URL in the settings from now on; call once after the identity is loaded.</summary>
    public void Start()
    {
        _settings.Changed += s => Apply(s.RendezvousUrl);
        _presence.Changed += () => _ = UpdateWatchAsync();
        Apply(_settings.Current.RendezvousUrl);
    }

    /// <summary>Connects again right away, e.g. from the "Connect" button after an error.</summary>
    public void Reconnect()
    {
        string? url;
        lock (_gate)
        {
            url = _url;
            _url = null;
        }
        Apply(url);
    }

    public async Task<bool> SendAsync(string address, string data, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || State != SignalingState.Connected)
            return false;
        try
        {
            await SendFrameAsync(socket, new RvSend(address, data, Interlocked.Increment(ref _nextId)), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Apply(string? url)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        CancellationTokenSource? previous;
        Task previousLoop;
        lock (_gate)
        {
            if (url == _url && (url is null || _run is not null))
                return;
            _url = url;
            previous = _run;
            previousLoop = _loop;
            _run = url is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        }
        previous?.Cancel();
        if (url is null)
        {
            SetStatus(new RendezvousStatus(SignalingState.Off, null, null, null));
            ClearOnline();
            _relays.SetProvided([], default);
            return;
        }
        var token = _run!.Token;
        lock (_gate)
            _loop = Task.Run(async () =>
            {
                await previousLoop.ConfigureAwait(false);
                await RunAsync(new Uri(url), token).ConfigureAwait(false);
            }, CancellationToken.None);
    }

    private async Task RunAsync(Uri url, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(new RendezvousStatus(SignalingState.Connecting, url.ToString(), null, null));
            string? error;
            try
            {
                var loggedIn = await SessionAsync(url, cancellationToken).ConfigureAwait(false);
                if (loggedIn)
                    failures = 0;
                error = "The connection to the rendezvous service was closed.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RendezvousRefusedException e)
            {
                error = e.Message;
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpRequestException or IOException)
            {
                error = $"Can't reach the rendezvous service: {e.Message}";
            }
            finally
            {
                _socket = null;
                ClearOnline();
            }

            var delay = _options.RetryDelays[Math.Min(failures, _options.RetryDelays.Count - 1)];
            failures++;
            _logger.LogInformation("Rendezvous service: {Error} Next attempt in {Delay}", error, delay);
            SetStatus(new RendezvousStatus(SignalingState.Failed, url.ToString(), error, _time.GetUtcNow().UtcDateTime + delay));
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection: login, watch, then messages until it closes. True if the login succeeded.</summary>
    private async Task<bool> SessionAsync(Uri url, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(_options.ConnectTimeout);
            await socket.ConnectAsync(url, connect.Token).ConfigureAwait(false);
        }
        var buffer = new byte[RendezvousLimits.MaxFrameSize];

        RvMessage? first;
        using (var login = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            login.CancelAfter(_options.ConnectTimeout);
            first = await ReadAsync(socket, buffer, login.Token).ConfigureAwait(false);
            if (first is not RvChallenge { Nonce: { } nonce })
                throw new RendezvousRefusedException("This address is not a PairSync rendezvous service.");
            var identity = _identity.Value.Identity;
            await SendFrameAsync(socket, new RvHello(identity.PublicKey, identity.Sign(RendezvousAuth.SignedData(nonce)), ProtocolVersion.Current),
                login.Token).ConfigureAwait(false);
            var reply = await ReadAsync(socket, buffer, login.Token).ConfigureAwait(false);
            if (reply is RvError error)
                throw new RendezvousRefusedException($"The rendezvous service refused the login: {error.Message}");
            if (reply is not RvWelcome welcome)
                throw new RendezvousRefusedException("The rendezvous service did not confirm the login.");
            _relays.SetProvided([.. (welcome.IceServers ?? []).Select(s => new TurnServer(s.Uri, s.Username, s.Credential))],
                welcome.IceServersExpireAtUtc ?? default);
        }

        _socket = socket;
        lock (_gate)
            _watched = [];
        SetStatus(new RendezvousStatus(SignalingState.Connected, url.ToString(), null, null));
        _logger.LogInformation("Connected to the rendezvous service {Url}", url);
        await UpdateWatchAsync().ConfigureAwait(false);

        while (await ReadAsync(socket, buffer, cancellationToken).ConfigureAwait(false) is { } message)
        {
            switch (message)
            {
                case RvPresence { Address: { } address } presence:
                    OnPresence(address, presence.Online);
                    break;
                case RvDeliver { From: { } from, Data: { } data }:
                    MessageReceived?.Invoke(from, data);
                    break;
                case RvUndeliverable undeliverable:
                    _logger.LogDebug("Rendezvous message {Id} not delivered: the device is offline", undeliverable.Id);
                    break;
                case RvError { Code: RvErrorCodes.Replaced }:
                    throw new RendezvousRefusedException("This device logged in to the rendezvous service from another connection.");
                case RvError error:
                    _logger.LogWarning("Rendezvous service: {Code} {Message}", error.Code, error.Message);
                    break;
            }
        }
        return true;
    }

    /// <summary>Sends the addresses of the paired, not blocked devices if they changed.</summary>
    private async Task UpdateWatchAsync()
    {
        var socket = _socket;
        if (socket is null)
            return;
        string[] addresses = [.. _presence.PairedDevices.Where(d => d.Trust == DeviceTrust.Active).Select(AddressOf).Order(StringComparer.Ordinal)
            .Take(RendezvousLimits.MaxWatchedAddresses)];
        string[] removed;
        lock (_gate)
        {
            if (addresses.SequenceEqual(_watched))
                return;
            removed = [.. _watched.Except(addresses)];
            _watched = addresses;
            foreach (var address in removed)
                _online.Remove(address);
        }
        foreach (var address in removed)
            PresenceChanged?.Invoke(address, false);
        try
        {
            await SendFrameAsync(socket, new RvWatch(addresses), _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogDebug(e, "Could not update the rendezvous watch list");
        }
    }

    private void OnPresence(string address, bool online)
    {
        bool changed;
        lock (_gate)
            changed = _watched.Contains(address) && (online ? _online.Add(address) : _online.Remove(address));
        if (changed)
            PresenceChanged?.Invoke(address, online);
    }

    private void ClearOnline()
    {
        string[] offline;
        lock (_gate)
        {
            offline = [.. _online];
            _online.Clear();
            _watched = [];
        }
        foreach (var address in offline)
            PresenceChanged?.Invoke(address, false);
    }

    private void SetStatus(RendezvousStatus status)
    {
        lock (_gate)
        {
            if (_status == status)
                return;
            _status = status;
        }
        StateChanged?.Invoke();
    }

    private async Task SendFrameAsync(ClientWebSocket socket, RvMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(RvCodec.SerializeToUtf8(message), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>The next message, null once closed. Unknown messages are skipped.</summary>
    private static async Task<RvMessage?> ReadAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                    throw new RendezvousRefusedException("The rendezvous service sent a message that is too large.");
                var result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                length += result.Count;
                if (result.EndOfMessage)
                    break;
            }
            if (RvCodec.Deserialize(buffer.AsSpan(0, length)) is { } message)
                return message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task loop;
        lock (_gate)
            loop = _loop;
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stopping.Dispose();
        _sendLock.Dispose();
    }

    private sealed class RendezvousRefusedException(string message) : Exception(message);
}
