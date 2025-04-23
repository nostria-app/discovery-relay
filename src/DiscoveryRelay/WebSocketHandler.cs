using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DiscoveryRelay.Models;

namespace DiscoveryRelay;

public class WebSocketHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _clientSubscriptions = new();
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public WebSocketHandler(ILogger<WebSocketHandler> logger)
    {
        _logger = logger;
        
        // Configure JSON options for source-generated serialization
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = NostrSerializationContext.Default
        };
    }

    public async Task HandleWebSocketAsync(HttpContext context, WebSocket webSocket)
    {
        var socketId = Guid.NewGuid().ToString();
        _sockets.TryAdd(socketId, webSocket);
        _clientSubscriptions.TryAdd(socketId, new HashSet<string>());

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
            try
            {
                // Process the received message
                var receivedMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                _logger.LogInformation("Message received from {SocketId}: {Message}", socketId, receivedMessage);

                // Try to parse as Nostr message
                if (TryParseNostrMessage(socketId, receivedMessage, out string? responseMessage))
                {
                    // If we have a response, send it back
                    if (!string.IsNullOrEmpty(responseMessage))
                    {
                        var responseBytes = Encoding.UTF8.GetBytes(responseMessage);
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(responseBytes, 0, responseBytes.Length),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                }
                else
                {
                    // If not a Nostr message, echo the message back as before
                    var echoMessage = $"Echo: {receivedMessage}";
                    var echoBytes = Encoding.UTF8.GetBytes(echoMessage);
                    
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(echoBytes, 0, echoBytes.Length),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from {SocketId}: {Message}", socketId, ex.Message);
                
                // Send error message back to client
                var errorMessage = $"Error processing message: {ex.Message}";
                var errorBytes = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorBytes, 0, errorBytes.Length),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }

            // Get next message
            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), CancellationToken.None);
        }
    }

    private bool TryParseNostrMessage(string socketId, string message, out string? responseMessage)
    {
        responseMessage = null;
        
        try
        {
            // Try to parse as JSON array first
            var jsonDocument = JsonDocument.Parse(message);
            
            if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array || 
                jsonDocument.RootElement.GetArrayLength() < 2)
            {
                return false;
            }
            
            var messageType = jsonDocument.RootElement[0].GetString();
            
            // Handle REQ message
            if (messageType == "REQ" && jsonDocument.RootElement.GetArrayLength() >= 3)
            {
                var subscriptionId = jsonDocument.RootElement[1].GetString() ?? string.Empty;
                
                if (_clientSubscriptions.TryGetValue(socketId, out var subscriptions))
                {
                    subscriptions.Add(subscriptionId);
                    _logger.LogInformation("Client {SocketId} added subscription {SubscriptionId}, total subscriptions: {Count}", 
                        socketId, subscriptionId, subscriptions.Count);
                    
                    // Log the filter for debugging purposes
                    if (jsonDocument.RootElement.GetArrayLength() > 2)
                    {
                        _logger.LogDebug("Subscription filter: {Filter}", 
                            jsonDocument.RootElement[2].ToString());
                    }
                }
                
                responseMessage = $"Subscription {subscriptionId} registered";
                return true;
            }
            // Handle CLOSE message
            else if (messageType == "CLOSE" && jsonDocument.RootElement.GetArrayLength() >= 2)
            {
                var subscriptionId = jsonDocument.RootElement[1].GetString() ?? string.Empty;
                
                if (_clientSubscriptions.TryGetValue(socketId, out var subscriptions))
                {
                    subscriptions.Remove(subscriptionId);
                    _logger.LogInformation("Client {SocketId} removed subscription {SubscriptionId}, remaining subscriptions: {Count}", 
                        socketId, subscriptionId, subscriptions.Count);
                    
                    // If client has no more subscriptions, schedule disconnection
                    if (subscriptions.Count == 0)
                    {
                        _logger.LogInformation("Client {SocketId} has no more subscriptions, will disconnect", socketId);
                        // We'll close the connection after sending the response
                        // but we won't call CloseWebSocketAsync directly to avoid duplicate logging
                        Task.Run(async () => 
                        {
                            // Small delay to ensure response is sent before closing
                            await Task.Delay(500);
                            if (_sockets.TryGetValue(socketId, out var webSocket) && 
                                webSocket.State == WebSocketState.Open)
                            {
                                // Close the WebSocket but don't call our CloseWebSocketAsync method
                                // The connection will be fully cleaned up in the finally block
                                await webSocket.CloseAsync(
                                    WebSocketCloseStatus.NormalClosure,
                                    "No active subscriptions",
                                    CancellationToken.None);
                            }
                        });
                    }
                }
                
                responseMessage = $"Subscription {subscriptionId} closed";
                return true;
            }
            
            return false;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse message as Nostr protocol: {Message}", message);
            return false;
        }
    }

    private async Task CloseWebSocketAsync(string socketId, WebSocket webSocket)
    {
        // Only remove from collections and log if we haven't already done so
        if (_sockets.TryRemove(socketId, out _))
        {
            _clientSubscriptions.TryRemove(socketId, out _);
            _logger.LogInformation("WebSocket closed: {SocketId}", socketId);
        }

        if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Closing",
                CancellationToken.None);
        }
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
