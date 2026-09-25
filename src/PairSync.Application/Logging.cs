using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PairSync.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PairSync.Application;

/// <summary>
/// Microsoft.Extensions.Logging backed by a rolling Serilog file in <c>logs/</c>.
/// Rule (plan §11): never log file contents, keys, invitation payloads or pairing codes; paths and sizes are fine.
/// </summary>
public static class Logging
{
    public static IServiceCollection AddPairSyncLogging(this IServiceCollection services, DataDirectory dataDirectory, LoggingLevelSwitch level)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(level)
            // EF Core logs every SQL command at Information; only its problems are interesting.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(dataDirectory.LogsDirectory, "pairsync-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return services.AddLogging(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddSerilog(logger, dispose: true));
    }

    public static LogEventLevel LevelFor(bool verbose) => verbose ? LogEventLevel.Debug : LogEventLevel.Information;
}
