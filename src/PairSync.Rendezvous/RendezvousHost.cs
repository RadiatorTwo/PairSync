using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace PairSync.Rendezvous;

/// <summary>Builds the service: WebSocket at <c>/v1</c>, <c>/health</c> for monitoring. Program and tests share it.</summary>
public static class RendezvousHost
{
    public const string WebSocketPath = "/v1";

    public static WebApplication Build(string[] args, Action<RendezvousOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddOptions<RendezvousOptions>()
            .Bind(builder.Configuration.GetSection("Rendezvous"))
            .Configure(o => configure?.Invoke(o));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RendezvousHub>();

        var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<RendezvousOptions>>().Value;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.KeepAliveInterval });

        app.MapGet("/health", (RendezvousHub hub) => Results.Text($"ok {hub.OnlineCount}"));
        app.Map(WebSocketPath, async (HttpContext context, RendezvousHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await hub.RunAsync(socket, context.Connection.RemoteIpAddress, context.RequestAborted).ConfigureAwait(false);
        });
        return app;
    }

    /// <summary>Starts the service on a free loopback port, for tests; returns its <c>ws://…/v1</c> URL.</summary>
    public static async Task<(WebApplication App, Uri Url)> StartLocalAsync(Action<RendezvousOptions>? configure = null)
    {
        var app = Build(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning"], configure);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + WebSocketPath));
    }
}
