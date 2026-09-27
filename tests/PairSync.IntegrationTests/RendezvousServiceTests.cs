using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using PairSync.Domain;
using PairSync.Protocol.Rendezvous;
using PairSync.Rendezvous;

namespace PairSync.IntegrationTests;

/// <summary>The rendezvous service on a loopback port, spoken to with raw WebSockets (phase 5 block B).</summary>
public sealed class RendezvousServiceTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _url = null!;
    private readonly List<IDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => (_app, _url) = await RendezvousHost.StartLocalAsync(o =>
    {
        o.MessagesPerMinute = 20;
        o.Turn.Uris = ["turn:relay.example.org:3478"];
        o.Turn.Secret = "shared-secret";
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _owned)
            item.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<Peer> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        _owned.Add(socket);
        await socket.ConnectAsync(_url, Ct);
        return new Peer(socket);
    }

    private async Task<(Peer Peer, DeviceIdentity Identity, RvWelcome Welcome)> LoginAsync()
    {
        var identity = DeviceIdentity.Create();
        _owned.Add(identity);
        var peer = await ConnectAsync();
        var challenge = Assert.IsType<RvChallenge>(await peer.ReadAsync());
        await peer.SendAsync(new RvHello(identity.PublicKey, identity.Sign(RendezvousAuth.SignedData(challenge.Nonce)), "test"));
        return (peer, identity, Assert.IsType<RvWelcome>(await peer.ReadAsync()));
    }

    [Fact]
    public async Task Device_logs_in_under_the_hash_of_its_key_and_gets_relay_credentials()
    {
        var (_, identity, welcome) = await LoginAsync();

        Assert.Equal(RendezvousAuth.AddressOf(identity.PublicKey), welcome.Address);
        var relay = Assert.Single(welcome.IceServers);
        Assert.Equal("turn:relay.example.org:3478", relay.Uri);
        Assert.EndsWith(":" + welcome.Address, relay.Username, StringComparison.Ordinal);
        var expected = Convert.ToBase64String(HMACSHA1.HashData("shared-secret"u8, Encoding.UTF8.GetBytes(relay.Username)));
        Assert.Equal(expected, relay.Credential);
        Assert.True(welcome.IceServersExpireAtUtc > DateTime.UtcNow.AddHours(11));
    }

    [Fact]
    public async Task Wrong_signature_is_refused()
    {
        using var identity = DeviceIdentity.Create();
        using var other = DeviceIdentity.Create();
        var peer = await ConnectAsync();
        var challenge = Assert.IsType<RvChallenge>(await peer.ReadAsync());

        await peer.SendAsync(new RvHello(identity.PublicKey, other.Sign(RendezvousAuth.SignedData(challenge.Nonce)), "test"));

        Assert.Equal(RvErrorCodes.AuthFailed, Assert.IsType<RvError>(await peer.ReadAsync()).Code);
        Assert.Null(await peer.ReadAsync());
    }

    [Fact]
    public async Task Watcher_learns_presence_only_of_watched_addresses()
    {
        var (a, _, _) = await LoginAsync();
        var (b, _, bWelcome) = await LoginAsync();

        await a.SendAsync(new RvWatch([bWelcome.Address]));
        Assert.Equal(new RvPresence(bWelcome.Address, true), await a.ReadAsync());

        var (_, _, cWelcome) = await LoginAsync();
        await b.CloseAsync();
        Assert.Equal(new RvPresence(bWelcome.Address, false), await a.ReadAsync());

        await a.SendAsync(new RvWatch([cWelcome.Address, bWelcome.Address]));
        Assert.Equal(new RvPresence(cWelcome.Address, true), await a.ReadAsync());
        Assert.Equal(new RvPresence(bWelcome.Address, false), await a.ReadAsync());
    }

    [Fact]
    public async Task Message_reaches_the_connected_device_with_the_sender_address()
    {
        var (a, _, aWelcome) = await LoginAsync();
        var (b, _, bWelcome) = await LoginAsync();

        await a.SendAsync(new RvSend(bWelcome.Address, "PSC1:hello", 1));

        Assert.Equal(new RvDeliver(aWelcome.Address, "PSC1:hello"), await b.ReadAsync());
    }

    [Fact]
    public async Task Message_to_an_offline_device_is_reported_undeliverable()
    {
        var (a, _, _) = await LoginAsync();

        await a.SendAsync(new RvSend(new string('a', 64), "PSC1:hello", 7));

        Assert.Equal(new RvUndeliverable(7), await a.ReadAsync());
    }

    [Fact]
    public async Task New_login_of_the_same_device_replaces_the_old_connection_without_offline_blip()
    {
        var (watcher, _, _) = await LoginAsync();
        var (old, identity, welcome) = await LoginAsync();
        await watcher.SendAsync(new RvWatch([welcome.Address]));
        Assert.Equal(new RvPresence(welcome.Address, true), await watcher.ReadAsync());

        var fresh = await ConnectAsync();
        var challenge = Assert.IsType<RvChallenge>(await fresh.ReadAsync());
        await fresh.SendAsync(new RvHello(identity.PublicKey, identity.Sign(RendezvousAuth.SignedData(challenge.Nonce)), "test"));
        Assert.IsType<RvWelcome>(await fresh.ReadAsync());

        Assert.Equal(RvErrorCodes.Replaced, Assert.IsType<RvError>(await old.ReadAsync()).Code);
        await watcher.SendAsync(new RvSend(welcome.Address, "ping", 2));
        Assert.Equal("ping", Assert.IsType<RvDeliver>(await fresh.ReadAsync()).Data);
    }

    [Fact]
    public async Task Flooding_is_rate_limited()
    {
        var (a, _, _) = await LoginAsync();

        for (var i = 0; i < 25; i++)
            await a.SendAsync(new RvSend(new string('b', 64), "x", i));

        var replies = new List<RvMessage>();
        for (var i = 0; i < 25; i++)
            replies.Add((await a.ReadAsync())!);
        Assert.Equal(20, replies.OfType<RvUndeliverable>().Count());
        Assert.All(replies.OfType<RvError>(), e => Assert.Equal(RvErrorCodes.RateLimited, e.Code));
    }

    [Fact]
    public async Task Oversized_data_is_refused()
    {
        var (a, _, _) = await LoginAsync();

        await a.SendAsync(new RvSend(new string('b', 64), new string('x', RendezvousLimits.MaxDataLength + 1), 1));

        Assert.Equal(RvErrorCodes.TooLarge, Assert.IsType<RvError>(await a.ReadAsync()).Code);
    }

    [Fact]
    public async Task Health_endpoint_answers()
    {
        using var http = new HttpClient();
        var text = await http.GetStringAsync(new Uri(_url.ToString().Replace("ws://", "http://", StringComparison.Ordinal).Replace("/v1", "/health", StringComparison.Ordinal)), Ct);
        Assert.StartsWith("ok", text, StringComparison.Ordinal);
    }

    private sealed class Peer(ClientWebSocket socket)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];

        public Task SendAsync(RvMessage message) =>
            socket.SendAsync(RvCodec.SerializeToUtf8(message), WebSocketMessageType.Text, true, Ct);

        /// <summary>The next message, or null once the service closed the connection.</summary>
        public async Task<RvMessage?> ReadAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var length = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(_buffer.AsMemory(length), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                length += result.Count;
                if (result.EndOfMessage)
                    return RvCodec.Deserialize(_buffer.AsSpan(0, length));
            }
        }

        public Task CloseAsync() => socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
    }
}
