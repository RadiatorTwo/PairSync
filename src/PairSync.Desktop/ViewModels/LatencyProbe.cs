using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Discovery.Lan;

namespace PairSync.Desktop.ViewModels;

/// <summary>
/// Round-trip time for the "LAN · direct · 3 ms" line, by ICMP echo. Best effort: many firewalls drop echo
/// requests, and then the card shows the route without a time.
/// </summary>
public sealed class LatencyProbe
{
    private readonly ConcurrentDictionary<Guid, TimeSpan> _roundTrips = new();
    private int _running;

    /// <summary>A new measurement arrived.</summary>
    public event Action? Changed;

    public TimeSpan? RoundTrip(Guid deviceId) => _roundTrips.TryGetValue(deviceId, out var rtt) ? rtt : null;

    /// <summary>Pings each device once in the background; a probe still running is not repeated.</summary>
    public void Probe(IEnumerable<(Guid DeviceId, LanServiceInfo Lan)> devices)
    {
        var targets = devices.ToList();
        if (targets.Count == 0 || Interlocked.Exchange(ref _running, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (id, lan) in targets)
                {
                    if (lan.Addresses.Count == 0)
                        continue;
                    try
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(lan.Addresses[0], TimeSpan.FromSeconds(1), null, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success)
                            _roundTrips[id] = TimeSpan.FromMilliseconds(reply.RoundtripTime);
                        else
                            _roundTrips.TryRemove(id, out _);
                    }
                    catch (Exception e) when (e is PingException or InvalidOperationException or NotSupportedException)
                    {
                        _roundTrips.TryRemove(id, out _);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
            Changed?.Invoke();
        });
    }
}

internal static class CoreExtensions
{
    public static ILogger<T> Logger<T>(this PairSyncCore core) => core.Services.GetRequiredService<ILogger<T>>();
}
