using Microsoft.Extensions.DependencyInjection;
using PairSync.Storage.Identity;
using PairSync.Storage.Secrets;
using PairSync.Storage.Settings;
using PairSync.SyncEngine;

namespace PairSync.Storage;

public static class StorageServices
{
    public static IServiceCollection AddPairSyncStorage(this IServiceCollection services, DataDirectory dataDirectory)
    {
        services.AddSingleton(dataDirectory);
        services.AddDbContextFactory<PairSyncDbContext>(options => Database.Configure(options, dataDirectory.DatabasePath));
        services.AddSingleton<IChunkJournal, SqliteChunkJournal>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ISecretStoreProvider, PlatformSecretStores>();
        services.AddSingleton<DeviceIdentityStore>();
        return services;
    }
}
