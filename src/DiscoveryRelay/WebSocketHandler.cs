using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using DiscoveryRelay.Services;
using Microsoft.Extensions.Options;

namespace DiscoveryRelay;

public class WebSocketHandler : IDisposable
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _clientSubscriptions = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastActivityTime = new();
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Timer _statsTimer;
    private readonly Timer _idleConnectionTimer;
    private readonly RelayOptions _options;
    private readonly LmdbStorageService _storageService;

    public WebSocketHandler(
        ILogger<WebSocketHandler> logger, 
        IOptions<RelayOptions> options,
        LmdbStorageService storageService)
    {
        _logger = logger;
        _options = options.Value;
        _storageService = storageService;
        
        // Configure JSON options for source-generated serialization
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = NostrSerializationContext.Default
        };
        
        // Setup timer for periodic logging based on configuration
        var statsInterval = TimeSpan.FromMinutes(_options.StatsLogIntervalMinutes);
        _statsTimer = new Timer(LogConnectionStats, null, statsInterval, statsInterval);
        
        // Setup timer for checking idle connections (run every 30 seconds)
        _idleConnectionTimer = new Timer(CheckIdleConnections, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void LogConnectionStats(object? state)
    {
        var connectionCount = _sockets.Count;
        var totalSubscriptions = _clientSubscriptions.Values.Sum(x => x.Count);
        
        _logger.LogInformation("Active connections: {ConnectionCount}, Total subscriptions: {SubscriptionCount}", 
            connectionCount, totalSubscriptions);
    }
    
    private void CheckIdleConnections(object? state)
    {
        var idleTimeout = TimeSpan.FromMinutes(_options.DisconnectTimeoutMinutes);
        var now = DateTime.UtcNow;

        foreach (var (socketId, lastActivity) in _lastActivityTime)
        {
            // If the connection has been idle for longer than the timeout
            if (now - lastActivity > idleTimeout)
            {
                if (_sockets.TryGetValue(socketId, out var webSocket) && 
                    webSocket.State == WebSocketState.Open)
                {
                    _logger.LogInformation("Disconnecting idle client {SocketId} (last activity: {LastActivity})", 
                        socketId, lastActivity);

                    // Send "CLOSE" event to the subscriber
                    if (_clientSubscriptions.TryGetValue(socketId, out var subscriptions))
                    {
                        foreach (var subscriptionId in subscriptions)
                        {
                            var closeMessage = new[] { "CLOSED", subscriptionId, "error: shutting down idle subscription" };
                            var closeMessageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(closeMessage, _jsonOptions));

                            Task.Run(async () =>
                            {
                                try
                                {
                                    await webSocket.SendAsync(
                                        new ArraySegment<byte>(closeMessageBytes),
                                        WebSocketMessageType.Text,
                                        true,
                                        CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error sending CLOSE event to {SocketId} for subscription {SubscriptionId}", 
                                        socketId, subscriptionId);
                                }
                            });
                        }
                    }

                    // Close the connection asynchronously
                    Task.Run(async () =>
                    {
                        try
                        {
                            await CloseWebSocketAsync(socketId, webSocket, "Connection idle timeout");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error closing idle connection {SocketId}", socketId);
                        }
                    });
                }
                else
                {
                    // If the socket doesn't exist or is not open, remove the activity tracking
                    _lastActivityTime.TryRemove(socketId, out _);
                }
            }
        }
    }

    public void Dispose()
    {
        _statsTimer?.Dispose();
        _idleConnectionTimer?.Dispose();
    }

    public async Task HandleWebSocketAsync(HttpContext context, WebSocket webSocket)
    {
        var socketId = Guid.NewGuid().ToString();
        _sockets.TryAdd(socketId, webSocket);
        _clientSubscriptions.TryAdd(socketId, new HashSet<string>());
        
        // Track the initial connection time
        _lastActivityTime[socketId] = DateTime.UtcNow;

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
                // Update last activity time on any message received
                _lastActivityTime[socketId] = DateTime.UtcNow;
                
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
            
            // Handle EVENT message
            if (messageType == "EVENT" && jsonDocument.RootElement.GetArrayLength() >= 2)
            {
                try
                {
                    // Parse the event object
                    var eventJson = jsonDocument.RootElement[1].GetRawText();
                    var nostrEvent = JsonSerializer.Deserialize<NostrEvent>(eventJson, _jsonOptions);
                    
                    if (nostrEvent == null)
                    {
                        responseMessage = CreateNostrErrorResponse("Invalid event format");
                        return true;
                    }
                    
                    _logger.LogInformation("Received EVENT from {SocketId}, kind: {Kind}, id: {Id}", 
                        socketId, nostrEvent.Kind, nostrEvent.Id);
                    
                    // Validate event
                    if (string.IsNullOrEmpty(nostrEvent.Id) || string.IsNullOrEmpty(nostrEvent.PubKey) || 
                        string.IsNullOrEmpty(nostrEvent.Signature))
                    {
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing required fields");
                        return true;
                    }
                    
                    // Validate signature (commented out for now as it depends on the implementation)
                    /*
                    if (!nostrEvent.VerifySignature())
                    {
                        responseMessage = CreateNostrErrorResponse("Invalid signature");
                        return true;
                    }
                    */
                    
                    // Store the event in LMDB
                    bool stored = _storageService.StoreEvent(nostrEvent);
                    
                    if (!stored)
                    {
                        _logger.LogWarning("Failed to store event {Id} in LMDB", nostrEvent.Id);
                        responseMessage = CreateNostrErrorResponse("Failed to store event");
                        return true;
                    }
                    
                    _logger.LogInformation("Event {Id} successfully stored in LMDB", nostrEvent.Id);
                    
                    // Create an OK message as per NIP-20
                    responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",true,\"\"]";
                    
                    // Broadcast the event to all clients with matching subscriptions
                    // This would be implemented later
                    
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing EVENT message");
                    responseMessage = CreateNostrErrorResponse($"Error processing event: {ex.Message}");
                    return true;
                }
            }
            
            // Handle REQ message
            else if (messageType == "REQ" && jsonDocument.RootElement.GetArrayLength() >= 3)
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

    private string CreateNostrErrorResponse(string errorMessage)
    {
        // Create a NIP-20 compliant error response
        return $"[\"NOTICE\",\"{errorMessage}\"]";
    }

    private async Task CloseWebSocketAsync(string socketId, WebSocket webSocket, string reason = "Closing")
    {
        // Only remove from collections and log if we haven't already done so
        if (_sockets.TryRemove(socketId, out _))
        {
            _clientSubscriptions.TryRemove(socketId, out _);
            _lastActivityTime.TryRemove(socketId, out _);
            _logger.LogInformation("WebSocket closed: {SocketId}, Reason: {Reason}", socketId, reason);
        }

        if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                reason,
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
