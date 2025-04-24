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
            _logger.LogInformation("WebSocket background service stopping due to application shutdown");
        }

        _logger.LogInformation("WebSocket background service stopping");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WebSocket background service shutdown initiated");

        // Allow some time for graceful shutdown of WebSocketHandler
        var timeout = TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(timeout);
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token).Token;

        try
        {
            // Call base implementation with a timeout to ensure we don't hang
            await base.StopAsync(combinedToken);
            _logger.LogInformation("WebSocket background service base.StopAsync completed successfully");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogWarning("WebSocket background service StopAsync timed out after {Timeout} seconds", timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during WebSocket background service shutdown");
        }
    }
}
