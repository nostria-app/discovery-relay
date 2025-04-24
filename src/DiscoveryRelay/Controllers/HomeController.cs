using DiscoveryRelay.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiscoveryRelay.Controllers;

[ApiController]
[Route("api")]
public class HomeController : ControllerBase
{
    private readonly WebSocketHandler _webSocketHandler;
    private readonly ILogger<HomeController> _logger;
    private readonly LmdbStorageService _storageService;

    public HomeController(WebSocketHandler webSocketHandler, LmdbStorageService storageService, ILogger<HomeController> logger)
    {
        _webSocketHandler = webSocketHandler;
        _storageService = storageService;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new { status = "online", timestamp = DateTime.UtcNow });
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        _logger.LogInformation("Retrieving relay statistics");

        var result = new Dictionary<string, object>
        {
            { "timestamp", DateTime.UtcNow },
            { "connections", new {
                activeConnections = _webSocketHandler.GetActiveConnectionCount(),
                totalSubscriptions = _webSocketHandler.GetTotalSubscriptionCount()
            }},
            { "database", _storageService.GetDatabaseStats() }
        };

        return Ok(result);
    }

    [HttpPost("broadcast")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest request)
    {
        if (string.IsNullOrEmpty(request.Message))
        {
            return BadRequest(new { error = "Message is required" });
        }

        _logger.LogInformation("Broadcasting message: {Message}", request.Message);
        await _webSocketHandler.BroadcastMessageAsync(request.Message);
        
        return Ok(new { success = true });
    }
}

// This class needs to be public for source generation to work properly
public class BroadcastRequest
{
    public string? Message { get; set; }
}
