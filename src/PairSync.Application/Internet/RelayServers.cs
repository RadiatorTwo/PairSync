using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PairSync.Storage.Secrets;
using PairSync.Stun;
using PairSync.Transport;

namespace PairSync.Application.Internet;

/// <summary>A TURN relay with its credentials; <see cref="Uri"/> is a <see cref="TurnServerUri"/> without user info.</summary>
public sealed record TurnServer(string Uri, string Username, string Password)
{
    /// <summary>The entry for the ICE configuration, or null if <see cref="Uri"/> is not a valid TURN URI.</summary>
    public string? ToIceServer() => TurnServerUri.TryParse(Uri, out var uri) ? uri.ToIceServer(Username, Password) : null;
}

/// <summary>
/// TURN relays for internet connections (phase 5 block A): the ones the user entered, kept in the secret store
/// because they carry passwords, plus short-lived ones handed out by the rendezvous service. Relays only see
/// encrypted packets; ICE uses them only when no direct path works.
/// </summary>
public sealed class RelayServers(ISecretStoreProvider secrets, TimeProvider time, ILogger<RelayServers> logger) : IRelayConfigurationProvider
{
    public const string SecretName = "turn-servers";
    public const int MaxServers = 8;

    private readonly Lock _gate = new();
    private IReadOnlyList<TurnServer> _configured = [];
    private IReadOnlyList<TurnServer> _provided = [];
    private DateTime _providedUntilUtc;

    /// <summary>The relays the user entered.</summary>
    public IReadOnlyList<TurnServer> Configured
    {
        get
        {
            lock (_gate)
                return _configured;
        }
    }

    /// <summary>Relays from the rendezvous service that have not expired.</summary>
    public IReadOnlyList<TurnServer> Provided
    {
        get
        {
            lock (_gate)
                return time.GetUtcNow().UtcDateTime < _providedUntilUtc ? _provided : [];
        }
    }

    /// <summary>Why the stored relays could not be read, e.g. a locked keyring; null if fine.</summary>
    public string? LoadError { get; private set; }

    public event Action? Changed;

    /// <summary>Reads the stored relays. Never throws: a locked keyring means no relays and <see cref="LoadError"/>.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var store = await secrets.OpenPreferredAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await store.LoadAsync(SecretName, cancellationToken).ConfigureAwait(false);
            var list = bytes is null ? [] : JsonSerializer.Deserialize(bytes, RelayJsonContext.Default.ListTurnServer) ?? [];
            lock (_gate)
                _configured = [.. list.Where(IsValid).Take(MaxServers)];
            LoadError = null;
        }
        catch (Exception e) when (e is SecretStoreUnavailableException or JsonException or IOException)
        {
            LoadError = e.Message;
            logger.LogWarning(e, "Could not read the relay servers");
        }
        Changed?.Invoke();
    }

    /// <summary>Replaces the relays the user entered.</summary>
    /// <exception cref="ArgumentException">An entry is not a valid TURN URI or lacks a user name.</exception>
    /// <exception cref="SecretStoreUnavailableException">The secret store cannot be written right now.</exception>
    public async Task SaveAsync(IReadOnlyList<TurnServer> servers, CancellationToken cancellationToken)
    {
        if (servers.Count > MaxServers)
            throw new ArgumentException($"At most {MaxServers} relays.", nameof(servers));
        var invalid = servers.FirstOrDefault(s => !IsValid(s));
        if (invalid is not null)
            throw new ArgumentException($"Not a valid relay: {invalid.Uri}", nameof(servers));
        List<TurnServer> list = [.. servers.Select(s => s with { Uri = TurnServerUri.Parse(s.Uri).ToString(), Username = s.Username.Trim() })];

        using (var store = await secrets.OpenPreferredAsync(cancellationToken).ConfigureAwait(false))
        {
            if (list.Count == 0)
                await store.DeleteAsync(SecretName, cancellationToken).ConfigureAwait(false);
            else
                await store.SaveAsync(SecretName, JsonSerializer.SerializeToUtf8Bytes(list, RelayJsonContext.Default.ListTurnServer), cancellationToken)
                    .ConfigureAwait(false);
        }
        lock (_gate)
            _configured = list;
        LoadError = null;
        Changed?.Invoke();
    }

    /// <summary>The rendezvous service handed out relays valid until <paramref name="untilUtc"/>; empty clears them.</summary>
    public void SetProvided(IReadOnlyList<TurnServer> servers, DateTime untilUtc)
    {
        lock (_gate)
        {
            _provided = [.. servers.Where(IsValid).Take(MaxServers)];
            _providedUntilUtc = untilUtc;
        }
        Changed?.Invoke();
    }

    /// <summary>Entries for the ICE configuration: configured relays first, then provided ones.</summary>
    public IReadOnlyList<string> IceServers() =>
        [.. Configured.Concat(Provided).Select(s => s.ToIceServer()).OfType<string>().Distinct(StringComparer.Ordinal)];

    public Task<IReadOnlyList<string>> GetRelayServersAsync(CancellationToken cancellationToken) => Task.FromResult(IceServers());

    private static bool IsValid(TurnServer server) =>
        server is { Uri: not null, Username: not null, Password: not null }
        && !string.IsNullOrWhiteSpace(server.Username)
        && TurnServerUri.TryParse(server.Uri, out _);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<TurnServer>))]
internal sealed partial class RelayJsonContext : JsonSerializerContext;
