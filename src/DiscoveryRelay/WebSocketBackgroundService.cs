using Microsoft.Extensions.Hosting;

namespace DiscoveryRelay;

/// <summary>
/// Background service to ensure the WebSocketHandler is properly initialized and disposed
/// </summary>
public class WebSocketBackgroundService : BackgroundService
{
    private readonly WebSocketHandler _webSocketHandler;
    private readonly ILogger<WebSocketBackgroundService> _logger;

    public WebSocketBackgroundService(WebSocketHandler webSocketHandler, ILogger<WebSocketBackgroundService> logger)
    {
        _webSocketHandler = webSocketHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebSocket background service started");
        
        // This service doesn't need to do anything actively
        // It just ensures WebSocketHandler is initialized on startup
        
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when application is shutting down
        }
        
        _logger.LogInformation("WebSocket background service stopping");
    }
}
