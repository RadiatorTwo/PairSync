using Microsoft.Extensions.Logging.Abstractions;
using PairSync.Application.Internet;
using PairSync.Storage.Secrets;

namespace PairSync.UnitTests;

public sealed class RelayServersTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-relays-");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Delete(recursive: true);

    private RelayServers Create() => new(new FileSecrets(_root.FullName), _time, NullLogger<RelayServers>.Instance);

    [Fact]
    public async Task Saved_relays_are_normalized_and_survive_a_restart()
    {
        await Create().SaveAsync([new TurnServer(" TURN:Relay.Example.org ", " alice ", "s3cret")], Ct);

        var relays = Create();
        await relays.LoadAsync(Ct);

        var server = Assert.Single(relays.Configured);
        Assert.Equal(new TurnServer("turn:relay.example.org:3478", "alice", "s3cret"), server);
        Assert.Equal(["turn:alice:s3cret@relay.example.org:3478"], relays.IceServers());
        Assert.Null(relays.LoadError);
    }

    [Fact]
    public async Task Password_is_not_written_to_a_readable_settings_file()
    {
        await Create().SaveAsync([new TurnServer("turn:relay.example.org", "alice", "s3cret-value")], Ct);

        Assert.DoesNotContain(Directory.EnumerateFiles(_root.FullName, "*.json", SearchOption.AllDirectories),
            f => File.ReadAllText(f).Contains("s3cret-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_entry_is_rejected()
    {
        var relays = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => relays.SaveAsync([new TurnServer("stun:relay.example.org", "alice", "x")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => relays.SaveAsync([new TurnServer("turn:relay.example.org", " ", "x")], Ct));
        Assert.Empty(relays.Configured);
    }

    [Fact]
    public async Task Provided_relays_expire()
    {
        var relays = Create();
        relays.SetProvided([new TurnServer("turn:rv.example.org", "1790000000:ab", "pw")], _time.GetUtcNow().UtcDateTime.AddHours(1));

        Assert.Equal(["turn:1790000000%3Aab:pw@rv.example.org:3478"], relays.IceServers());

        _time.Advance(TimeSpan.FromHours(2));
        Assert.Empty(relays.IceServers());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Locked_store_means_no_relays_and_an_error()
    {
        var relays = new RelayServers(new LockedSecrets(), _time, NullLogger<RelayServers>.Instance);

        await relays.LoadAsync(Ct);

        Assert.Empty(relays.Configured);
        Assert.NotNull(relays.LoadError);
    }

    private sealed class FileSecrets(string root) : ISecretStoreProvider
    {
        public Task<ISecretStore> OpenPreferredAsync(CancellationToken cancellationToken) => Task.FromResult<ISecretStore>(new FileSecretStore(root));

        public Task<ISecretStore> OpenAsync(SecretProtection protection, CancellationToken cancellationToken) => OpenPreferredAsync(cancellationToken);
    }

    private sealed class LockedSecrets : ISecretStoreProvider
    {
        public Task<ISecretStore> OpenPreferredAsync(CancellationToken cancellationToken) =>
            throw new SecretStoreUnavailableException("The keyring is locked.");

        public Task<ISecretStore> OpenAsync(SecretProtection protection, CancellationToken cancellationToken) => OpenPreferredAsync(cancellationToken);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
