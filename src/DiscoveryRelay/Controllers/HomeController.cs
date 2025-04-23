using Microsoft.AspNetCore.Mvc;

namespace DiscoveryRelay.Controllers;

[ApiController]
[Route("api")]
public class HomeController : ControllerBase
{
    private readonly WebSocketHandler _webSocketHandler;
    private readonly ILogger<HomeController> _logger;

    public HomeController(WebSocketHandler webSocketHandler, ILogger<HomeController> logger)
    {
        _webSocketHandler = webSocketHandler;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new { status = "online", timestamp = DateTime.UtcNow });
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

public class BroadcastRequest
{
    public string? Message { get; set; }
}
