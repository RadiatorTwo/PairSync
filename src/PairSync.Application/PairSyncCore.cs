using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Application.Pairing;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using Serilog.Core;

namespace PairSync.Application;

/// <summary>
/// The application core without UI: services, database and settings for one data directory.
/// The desktop app creates one; integration tests create two side by side.
/// </summary>
public sealed class PairSyncCore : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly SettingsStore _settings;

    private PairSyncCore(ServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<SettingsStore>();
    }

    public IServiceProvider Services => _services;

    public DataDirectory DataDirectory => _services.GetRequiredService<DataDirectory>();

    public SettingsStore Settings => _settings;

    /// <summary>This device. Set once <see cref="StartAsync"/> has returned.</summary>
    public LocalIdentity Identity { get; private set; } = null!;

    /// <param name="configure">Replaces services after the defaults, e.g. the secret store in tests.</param>
    /// <exception cref="IdentityUnavailableException">The device identity exists but its private key cannot be loaded.</exception>
    public static async Task<PairSyncCore> StartAsync(
        DataDirectory dataDirectory, CancellationToken cancellationToken, Action<IServiceCollection>? configure = null)
    {
        dataDirectory.EnsureCreated();

        var level = new LoggingLevelSwitch();
        var collection = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(level)
            .AddPairSyncLogging(dataDirectory, level)
            .AddPairSyncStorage(dataDirectory)
            .AddPairSyncConnections();
        configure?.Invoke(collection);
        var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var core = new PairSyncCore(services);
        try
        {
            level.MinimumLevel = Logging.LevelFor(core.Settings.Current.VerboseLogging);
            core.Settings.Changed += settings => level.MinimumLevel = Logging.LevelFor(settings.VerboseLogging);

            await Database.MigrateAsync(services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>(), cancellationToken)
                .ConfigureAwait(false);

            core.Identity = await services.GetRequiredService<DeviceIdentityStore>().LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            services.GetRequiredService<CurrentIdentity>().Set(core.Identity);

            // Unpaired devices only get to pair; sessions of paired devices are for transfers.
            var lan = services.GetRequiredService<LanConnectionService>();
            var pairing = services.GetRequiredService<PairingService>();
            var transfers = services.GetRequiredService<TransferService>();
            lan.IncomingConnectionHandler = connection => connection.Access == PeerAccess.PairingOnly
                ? pairing.HandleIncomingAsync(connection)
                : transfers.HandleIncomingAsync(connection);

            // A busy port is not fatal: the app still works as a sender and Settings shows the error.
            await lan.StartAsync(cancellationToken).ConfigureAwait(false);
            await services.GetRequiredService<PresenceService>().StartAsync(cancellationToken).ConfigureAwait(false);
            await transfers.StartAsync(cancellationToken).ConfigureAwait(false);

            services.GetRequiredService<ILogger<PairSyncCore>>()
                .LogInformation("PairSync core started, data directory {DataDirectory}", dataDirectory.Root);
            return core;
        }
        catch
        {
            await core.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public LanConnectionService Lan => _services.GetRequiredService<LanConnectionService>();

    public PresenceService Presence => _services.GetRequiredService<PresenceService>();

    public PairingService Pairing => _services.GetRequiredService<PairingService>();

    public TransferService Transfers => _services.GetRequiredService<TransferService>();

    public Devices.DeviceService Devices => _services.GetRequiredService<Devices.DeviceService>();

    public async ValueTask DisposeAsync()
    {
        // Stop the active services in order while the provider still works: a disposing ServiceProvider refuses to
        // create anything, so a transfer could not save its last journal entry and presence not its last-seen times.
        // Services before the provider: the listener still uses the identity until it stops.
        if (_services.GetService<TransferService>() is { } transfers)
            await transfers.DisposeAsync().ConfigureAwait(false);
        await _services.GetRequiredService<PairingService>().DisposeAsync().ConfigureAwait(false);
        await _services.GetRequiredService<PresenceService>().DisposeAsync().ConfigureAwait(false);
        await _services.GetRequiredService<LanConnectionService>().DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
        Identity?.Identity.Dispose();
    }
}
