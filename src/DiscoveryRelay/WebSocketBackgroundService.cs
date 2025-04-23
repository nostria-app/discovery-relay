using Microsoft.Extensions.Hosting;

namespace DiscoveryRelay;

/// <summary>
/// Background service to ensure the WebSocketHandler is properly initialized and disposed
/// </summary>
public class WebSocketBackgroundService : BackgroundService
{
    private readonly WebSocketHandler _webSocketHandler;

    public WebSocketBackgroundService(WebSocketHandler webSocketHandler)
    {
        _webSocketHandler = webSocketHandler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This service doesn't need to do anything actively
        // It just ensures WebSocketHandler is initialized on startup
        await Task.CompletedTask;
    }
}
