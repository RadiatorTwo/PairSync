using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PairSync.Protocol.Rendezvous;

namespace PairSync.Rendezvous;

/// <summary>
/// The service's state, all in memory: logged-in devices by address and who watches whom. It relays presence and
/// messages and stores nothing; logs never contain message data.
/// </summary>
public sealed class RendezvousHub(IOptions<RendezvousOptions> options, TimeProvider time, ILogger<RendezvousHub> logger)
{
    private readonly RendezvousOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Client> _online = [];
    private readonly Dictionary<string, HashSet<Client>> _watchers = [];
    private readonly Dictionary<IPAddress, int> _perIp = [];
    private int _connections;

    /// <summary>Logged-in devices, for <c>/health</c>.</summary>
    public int OnlineCount
    {
        get
        {
            lock (_gate)
                return _online.Count;
        }
    }

    /// <summary>Runs one WebSocket connection until it closes.</summary>
    public async Task RunAsync(WebSocket socket, IPAddress? remote, CancellationToken aborted)
    {
        var ip = remote ?? IPAddress.None;
        if (!TryAdmit(ip))
        {
            await CloseAsync(socket, new RvError(RvErrorCodes.Busy, "Too many connections."), aborted).ConfigureAwait(false);
            return;
        }
        var client = new Client(socket, _options.MessagesPerMinute, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var writer = client.WriteLoopAsync(stop.Token);
        try
        {
            if (await LoginAsync(client, stop.Token).ConfigureAwait(false))
                await ReadLoopAsync(client, stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            Leave(client);
            client.Complete();
            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or TimeoutException)
            {
            }
            await stop.CancelAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _connections--;
                if (--_perIp[ip] == 0)
                    _perIp.Remove(ip);
            }
        }
    }

    private bool TryAdmit(IPAddress ip)
    {
        lock (_gate)
        {
            var count = _perIp.GetValueOrDefault(ip);
            if (count >= _options.MaxConnectionsPerIp || _connections >= _options.MaxConnections)
                return false;
            _perIp[ip] = count + 1;
            _connections++;
            return true;
        }
    }

    private async Task<bool> LoginAsync(Client client, CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(RendezvousLimits.NonceSize);
        client.Post(new RvChallenge(nonce, RendezvousLimits.ProtocolVersion));

        RvMessage? message;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.LoginTimeout);
            try
            {
                message = await client.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.PostAndClose(new RvError(RvErrorCodes.AuthFailed, "No login in time."));
                return false;
            }
        }
        if (message is not RvHello { PublicKey: { } key, Signature: { } signature } || !RendezvousAuth.Verify(key, nonce, signature))
        {
            client.PostAndClose(new RvError(RvErrorCodes.AuthFailed, "Login failed."));
            return false;
        }

        var address = RendezvousAuth.AddressOf(key);
        client.Address = address;
        Client? previous;
        HashSet<Client>? watchers;
        lock (_gate)
        {
            _online.Remove(address, out previous);
            _online[address] = client;
            watchers = _watchers.TryGetValue(address, out var set) ? [.. set] : null;
        }
        // The same device again (new network, restart): the old connection ends, watchers see no offline blip.
        previous?.PostAndClose(new RvError(RvErrorCodes.Replaced, "Logged in from another connection."));
        var (servers, expires) = _options.Turn.CredentialsFor(address, time.GetUtcNow());
        client.Post(new RvWelcome(address, servers, expires));
        if (previous is null && watchers is not null)
        {
            foreach (var watcher in watchers)
                watcher.Post(new RvPresence(address, true));
        }
        logger.LogDebug("Device {Address} logged in", Short(address));
        return true;
    }

    private async Task ReadLoopAsync(Client client, CancellationToken cancellationToken)
    {
        while (await client.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            if (!client.TryConsume())
            {
                client.Post(new RvError(RvErrorCodes.RateLimited, "Too many messages; slow down."));
                continue;
            }
            switch (message)
            {
                case RvWatch { Addresses: { } addresses }:
                    Watch(client, addresses);
                    break;
                case RvSend { To: { } to, Data: { } data } send:
                    if (data.Length > RendezvousLimits.MaxDataLength)
                        client.Post(new RvError(RvErrorCodes.TooLarge, "Message too large."));
                    else
                        Deliver(client, to, data, send.Id);
                    break;
                default:
                    client.Post(new RvError(RvErrorCodes.BadRequest, "Unexpected message."));
                    break;
            }
        }
    }

    private void Watch(Client client, IReadOnlyList<string> addresses)
    {
        if (addresses.Count > RendezvousLimits.MaxWatchedAddresses || !addresses.All(RendezvousAuth.IsAddress))
        {
            client.Post(new RvError(RvErrorCodes.BadRequest, "Invalid watch list."));
            return;
        }
        List<RvPresence> states;
        lock (_gate)
        {
            if (!IsCurrent(client))
                return;
            foreach (var old in client.Watched)
                Unwatch(old, client);
            client.Watched = [.. addresses];
            foreach (var address in client.Watched)
            {
                if (!_watchers.TryGetValue(address, out var set))
                    _watchers[address] = set = [];
                set.Add(client);
            }
            states = [.. client.Watched.Select(a => new RvPresence(a, _online.ContainsKey(a)))];
        }
        foreach (var state in states)
            client.Post(state);
    }

    private void Deliver(Client client, string to, string data, long id)
    {
        Client? target;
        lock (_gate)
            target = RendezvousAuth.IsAddress(to) ? _online.GetValueOrDefault(to) : null;
        if (target is null || ReferenceEquals(target, client))
            client.Post(new RvUndeliverable(id));
        else
            target.Post(new RvDeliver(client.Address!, data));
    }

    private void Leave(Client client)
    {
        HashSet<Client>? watchers = null;
        lock (_gate)
        {
            foreach (var watched in client.Watched)
                Unwatch(watched, client);
            client.Watched = [];
            if (client.Address is { } address && _online.TryGetValue(address, out var current) && ReferenceEquals(current, client))
            {
                _online.Remove(address);
                watchers = _watchers.TryGetValue(address, out var set) ? [.. set] : null;
            }
        }
        if (watchers is null)
            return;
        foreach (var watcher in watchers)
            watcher.Post(new RvPresence(client.Address!, false));
        logger.LogDebug("Device {Address} left", Short(client.Address!));
    }

    private bool IsCurrent(Client client) =>
        client.Address is { } address && _online.TryGetValue(address, out var current) && ReferenceEquals(current, client);

    private void Unwatch(string address, Client client)
    {
        if (_watchers.TryGetValue(address, out var set) && set.Remove(client) && set.Count == 0)
            _watchers.Remove(address);
    }

    private static async Task CloseAsync(WebSocket socket, RvMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(RvCodec.SerializeToUtf8(message), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static string Short(string address) => address[..12];

    /// <summary>One connection: frames in, a queue of messages out written by a single loop.</summary>
    private sealed class Client(WebSocket socket, int messagesPerMinute, TimeProvider time)
    {
        private readonly Channel<RvMessage?> _outbox = Channel.CreateBounded<RvMessage?>(
            new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        private readonly byte[] _buffer = new byte[RendezvousLimits.MaxFrameSize];
        private double _tokens = messagesPerMinute;
        private long _lastRefill = time.GetTimestamp();

        public string? Address { get; set; }

        public string[] Watched { get; set; } = [];

        public void Post(RvMessage message) => _outbox.Writer.TryWrite(message);

        /// <summary>Sends <paramref name="message"/> and then closes the connection.</summary>
        public void PostAndClose(RvMessage message)
        {
            _outbox.Writer.TryWrite(message);
            _outbox.Writer.TryWrite(null);
        }

        public void Complete() => _outbox.Writer.TryComplete();

        /// <summary>Token bucket: <c>messagesPerMinute</c> per minute, bursts up to the same number.</summary>
        public bool TryConsume()
        {
            var now = time.GetTimestamp();
            _tokens = Math.Min(messagesPerMinute, _tokens + time.GetElapsedTime(_lastRefill, now).TotalMinutes * messagesPerMinute);
            _lastRefill = now;
            if (_tokens < 1)
                return false;
            _tokens--;
            return true;
        }

        /// <summary>The next message; null when the connection closed. Oversized or malformed frames close it.</summary>
        public async Task<RvMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            var length = 0;
            while (true)
            {
                if (length == _buffer.Length)
                {
                    PostAndClose(new RvError(RvErrorCodes.TooLarge, "Frame too large."));
                    return null;
                }
                var result = await socket.ReceiveAsync(_buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                length += result.Count;
                if (result.EndOfMessage)
                    break;
            }
            var message = RvCodec.Deserialize(_buffer.AsSpan(0, length));
            if (message is null)
                PostAndClose(new RvError(RvErrorCodes.BadRequest, "Malformed message."));
            return message;
        }

        public async Task WriteLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message is null)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, null, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await socket.SendAsync(RvCodec.SerializeToUtf8(message), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
        }
    }
}
