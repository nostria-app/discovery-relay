using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace DiscoveryRelay;

public class WebSocketHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly ILogger<WebSocketHandler> _logger;

    public WebSocketHandler(ILogger<WebSocketHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleWebSocketAsync(HttpContext context, WebSocket webSocket)
    {
        var socketId = Guid.NewGuid().ToString();
        _sockets.TryAdd(socketId, webSocket);

        _logger.LogInformation("WebSocket connected: {SocketId}", socketId);

        try
        {
            await ProcessWebSocketAsync(socketId, webSocket);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket: {SocketId}", socketId);
        }
        finally
        {
            await CloseWebSocketAsync(socketId, webSocket);
        }
    }

    private async Task ProcessWebSocketAsync(string socketId, WebSocket webSocket)
    {
        var buffer = new byte[1024 * 4];
        var receiveResult = await webSocket.ReceiveAsync(
            new ArraySegment<byte>(buffer), CancellationToken.None);

        while (!receiveResult.CloseStatus.HasValue)
        {
            // Process the received message
            var receivedMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
            _logger.LogInformation("Message received from {SocketId}: {Message}", socketId, receivedMessage);

            // Echo the message back
            var responseMessage = $"Echo: {receivedMessage}";
            var responseBytes = Encoding.UTF8.GetBytes(responseMessage);
            
            await webSocket.SendAsync(
                new ArraySegment<byte>(responseBytes, 0, responseBytes.Length),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);

            // Get next message
            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), CancellationToken.None);
        }
    }

    private async Task CloseWebSocketAsync(string socketId, WebSocket webSocket)
    {
        _sockets.TryRemove(socketId, out _);

        if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Closing",
                CancellationToken.None);
        }

        _logger.LogInformation("WebSocket closed: {SocketId}", socketId);
    }

    public async Task BroadcastMessageAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var tasks = _sockets.Select(async socket =>
        {
            try
            {
                if (socket.Value.State == WebSocketState.Open)
                {
                    await socket.Value.SendAsync(
                        new ArraySegment<byte>(bytes, 0, bytes.Length),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to {SocketId}", socket.Key);
            }
        });

        await Task.WhenAll(tasks);
    }
}
