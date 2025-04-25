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
    private readonly CancellationTokenSource _shutdownTokenSource = new CancellationTokenSource();

    // Add allowed event kinds
    private readonly HashSet<int> _allowedEventKinds = new() { 3, 10002 };

    private const int MaxBufferSize = 16 * 1024; // Set buffer to 16KB

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

    /// <summary>
    /// Gets the count of active WebSocket connections
    /// </summary>
    public int GetActiveConnectionCount()
    {
        return _sockets.Count;
    }

    /// <summary>
    /// Gets the total number of subscriptions across all clients
    /// </summary>
    public int GetTotalSubscriptionCount()
    {
        return _clientSubscriptions.Values.Sum(x => x.Count);
    }

    /// <summary>
    /// Gets detailed statistics about WebSocket connections and subscriptions
    /// </summary>
    public Dictionary<string, object> GetConnectionStats()
    {
        var stats = new Dictionary<string, object>
        {
            { "activeConnections", _sockets.Count },
            { "totalSubscriptions", _clientSubscriptions.Values.Sum(x => x.Count) },
            { "connectionAges", _lastActivityTime.ToDictionary(
                kvp => kvp.Key,
                kvp => (DateTime.UtcNow - kvp.Value).TotalMinutes)
            }
        };

        var subscriptionsByClient = new Dictionary<string, int>();
        foreach (var client in _clientSubscriptions)
        {
            subscriptionsByClient[client.Key] = client.Value.Count;
        }

        stats["subscriptionsByClient"] = subscriptionsByClient;
        return stats;
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
        _logger.LogInformation("WebSocketHandler is being disposed");

        // Signal cancellation to all ongoing operations
        _shutdownTokenSource.Cancel();

        // Stop timers immediately
        _statsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _idleConnectionTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        // Close all WebSocket connections
        CloseAllSockets().GetAwaiter().GetResult();

        // Finally dispose of the timers and cancellation token source
        _statsTimer?.Dispose();
        _idleConnectionTimer?.Dispose();
        _shutdownTokenSource.Dispose();

        _logger.LogInformation("WebSocketHandler disposed successfully");
    }

    private async Task CloseAllSockets()
    {
        if (_sockets.IsEmpty)
        {
            return;
        }

        _logger.LogInformation("Closing {Count} WebSocket connections due to shutdown", _sockets.Count);

        // Create a list of tasks to close all sockets with a short timeout
        var closeTasks = new List<Task>();

        foreach (var (socketId, webSocket) in _sockets)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    var closeTask = webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server shutting down",
                        CancellationToken.None);

                    // Use a timeout to ensure we don't wait too long
                    var timeoutTask = Task.Delay(1000); // 1 second timeout

                    closeTasks.Add(Task.WhenAny(closeTask, timeoutTask));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket {SocketId} during shutdown", socketId);
            }
        }

        // Wait for all close operations to complete or timeout
        if (closeTasks.Count > 0)
        {
            await Task.WhenAll(closeTasks);
        }

        // Clear all collections
        _sockets.Clear();
        _clientSubscriptions.Clear();
        _lastActivityTime.Clear();

        _logger.LogInformation("All WebSocket connections closed");
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
            // Pass the shutdown token to ProcessWebSocketAsync
            await ProcessWebSocketAsync(socketId, webSocket, _shutdownTokenSource.Token);
        }
        catch (OperationCanceledException) when (_shutdownTokenSource.IsCancellationRequested)
        {
            // This is expected during shutdown, log at a lower level
            _logger.LogDebug("WebSocket {SocketId} processing canceled due to shutdown", socketId);
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

    private async Task ProcessWebSocketAsync(string socketId, WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBufferSize]; // Use the larger buffer size
        WebSocketReceiveResult receiveResult;

        // Create a memory stream to handle messages that span multiple frames
        using var messageBuffer = new MemoryStream();

        try
        {
            // Use the cancellation token for receiving messages
            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);

            while (!receiveResult.CloseStatus.HasValue)
            {
                try
                {
                    // Check cancellation frequently
                    cancellationToken.ThrowIfCancellationRequested();

                    // Update last activity time on any message received
                    _lastActivityTime[socketId] = DateTime.UtcNow;

                    // Add current frame to the message buffer
                    messageBuffer.Write(buffer, 0, receiveResult.Count);

                    // If this isn't the final frame, continue receiving
                    if (!receiveResult.EndOfMessage)
                    {
                        _logger.LogDebug("Received partial message from {SocketId}, continuing to next frame", socketId);
                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), cancellationToken);
                        continue;
                    }

                    // Process the complete message
                    var messageBytes = messageBuffer.ToArray();
                    var receivedMessage = Encoding.UTF8.GetString(messageBytes, 0, messageBytes.Length);

                    _logger.LogDebug("Message received from {SocketId} (size: {Size} bytes): {MessagePreview}...",
                        socketId, messageBytes.Length,
                        receivedMessage.Length > 100 ? receivedMessage.Substring(0, 100) : receivedMessage);

                    // Clear the memory stream for the next message
                    messageBuffer.SetLength(0);

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
                                cancellationToken);
                        }
                    }
                    else
                    {
                        // If not a Nostr message, echo the message back as before
                        var echoMessage = $"Echo: {(receivedMessage.Length > 100 ? receivedMessage.Substring(0, 100) + "..." : receivedMessage)}";
                        var echoBytes = Encoding.UTF8.GetBytes(echoMessage);

                        await webSocket.SendAsync(
                            new ArraySegment<byte>(echoBytes, 0, echoBytes.Length),
                            WebSocketMessageType.Text,
                            true,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // This is expected during shutdown, break out of the loop
                    _logger.LogDebug("Processing WebSocket {SocketId} canceled", socketId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message from {SocketId}: {Message}", socketId, ex.Message);

                    // Send error message back to client
                    var errorMessage = $"Error processing message: {ex.Message}";
                    var errorBytes = Encoding.UTF8.GetBytes(errorMessage);

                    try
                    {
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(errorBytes, 0, errorBytes.Length),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None); // Use a non-cancelable token for error responses
                    }
                    catch
                    {
                        // Ignore any errors while sending error message
                    }
                }

                // Get next message, using a short timeout combined with cancellation token
                try
                {
                    receiveResult = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Exit the loop if we're shutting down
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // This is expected when client disconnects abruptly
            _logger.LogInformation("WebSocket {SocketId} was closed prematurely by the client", socketId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This is expected during shutdown
            _logger.LogDebug("WebSocket {SocketId} processing canceled due to shutdown", socketId);
        }
    }

    private bool TryParseNostrMessage(string socketId, string message, out string? responseMessage)
    {
        responseMessage = null;

        try
        {
            // Add safeguard for message size
            if (message.Length > MaxBufferSize)
            {
                _logger.LogWarning("Message from {SocketId} exceeds maximum size ({Size} bytes)",
                    socketId, message.Length);
                responseMessage = CreateNostrErrorResponse("Message too large");
                return true;
            }

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
                    _logger.LogDebug("Raw event JSON: {EventJson}", eventJson);

                    var nostrEvent = JsonSerializer.Deserialize<NostrEvent>(eventJson, _jsonOptions);

                    if (nostrEvent == null)
                    {
                        _logger.LogWarning("EVENT deserialization failed - returned null object");
                        responseMessage = CreateNostrErrorResponse("Invalid event format");
                        return true;
                    }

                    _logger.LogDebug("Received EVENT from {SocketId}, kind: {Kind}, id: {Id}, pubkey: {PubKey}, created_at: {CreatedAt}, signature length: {SigLength}",
                        socketId, nostrEvent.Kind, nostrEvent.Id, nostrEvent.PubKey, nostrEvent.CreatedAt, nostrEvent.Signature?.Length ?? 0);

                    // Validate event
                    if (string.IsNullOrEmpty(nostrEvent.Id))
                    {
                        _logger.LogWarning("Rejected event: missing ID field");
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing ID field");
                        return true;
                    }

                    if (string.IsNullOrEmpty(nostrEvent.PubKey))
                    {
                        _logger.LogWarning("Rejected event {Id}: missing PubKey field", nostrEvent.Id);
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing PubKey field");
                        return true;
                    }

                    if (string.IsNullOrEmpty(nostrEvent.Signature))
                    {
                        _logger.LogWarning("Rejected event {Id}: missing Signature field", nostrEvent.Id);
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing Signature field");
                        return true;
                    }

                    // Check if the event kind is allowed
                    if (!_allowedEventKinds.Contains(nostrEvent.Kind))
                    {
                        _logger.LogWarning("Rejected event {Id} with unsupported kind: {Kind}", nostrEvent.Id, nostrEvent.Kind);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"restricted: only kinds 3 and 10002 are accepted\"]";
                        return true;
                    }

                    // Validate CreatedAt timestamp
                    if (nostrEvent.CreatedAt <= 0)
                    {
                        _logger.LogWarning("Rejected event {Id}: invalid or missing CreatedAt timestamp: {CreatedAt}",
                            nostrEvent.Id, nostrEvent.CreatedAt);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"invalid: created_at timestamp is invalid\"]";
                        return true;
                    }

                    // Validate signature
                    string? validationError = nostrEvent.VerifySignature();
                    if (validationError != null)
                    {
                        _logger.LogWarning("Rejected event {Id}: signature validation failed: {Error}",
                            nostrEvent.Id, validationError);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"invalid: {validationError}\"]";
                        return true;
                    }

                    // Validate the ID
                    string calculatedId = nostrEvent.CalculateId();
                    if (calculatedId != nostrEvent.Id)
                    {
                        _logger.LogWarning("Rejected event: ID mismatch. Provided: {ProvidedId}, Calculated: {CalculatedId}",
                            nostrEvent.Id, calculatedId);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"invalid: event id does not match calculated id\"]";
                        return true;
                    }

                    // Store the event in LMDB
                    _logger.LogDebug("Attempting to store event {Id} in LMDB", nostrEvent.Id);
                    bool stored = _storageService.StoreEvent(nostrEvent);

                    if (!stored)
                    {
                        _logger.LogWarning("Failed to store event {Id} in LMDB - storage service returned false", nostrEvent.Id);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"error: failed to store event\"]";
                        return true;
                    }

                    _logger.LogDebug("Event {Id} successfully stored in LMDB", nostrEvent.Id);

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

                // Check if any filter has kinds, and if so, ensure they're only the allowed kinds
                bool hasInvalidKinds = false;

                // Track if we have valid kinds and authors
                List<int> requestedKinds = new();
                HashSet<string> requestedAuthors = new();

                // Iterate through all filters in the REQ
                for (int i = 2; i < jsonDocument.RootElement.GetArrayLength(); i++)
                {
                    var filterElement = jsonDocument.RootElement[i];

                    // Check for kinds in the filter
                    if (filterElement.TryGetProperty("kinds", out var kindsElement))
                    {
                        if (kindsElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var kindElement in kindsElement.EnumerateArray())
                            {
                                if (kindElement.TryGetInt32(out int kind))
                                {
                                    if (_allowedEventKinds.Contains(kind))
                                    {
                                        requestedKinds.Add(kind);
                                    }
                                    else
                                    {
                                        hasInvalidKinds = true;
                                        _logger.LogWarning("Client {SocketId} attempted to subscribe to unsupported kind: {Kind}",
                                            socketId, kind);
                                    }
                                }
                            }
                        }
                    }

                    // Check for authors in the filter
                    if (filterElement.TryGetProperty("authors", out var authorsElement))
                    {
                        if (authorsElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var authorElement in authorsElement.EnumerateArray())
                            {
                                var author = authorElement.GetString();
                                if (!string.IsNullOrEmpty(author))
                                {
                                    requestedAuthors.Add(author);
                                }
                            }
                        }
                    }
                }

                if (hasInvalidKinds)
                {
                    responseMessage = CreateNostrErrorResponse("restricted: only kinds 3 and 10002 are supported");
                    return true;
                }

                if (_clientSubscriptions.TryGetValue(socketId, out var subscriptions))
                {
                    // Add subscription if not already present
                    if (!subscriptions.Contains(subscriptionId))
                    {
                        subscriptions.Add(subscriptionId);
                        _logger.LogInformation("Client {SocketId} added subscription {SubscriptionId}, total subscriptions: {Count}",
                            socketId, subscriptionId, subscriptions.Count);
                    }
                }

                // Process the request and retrieve matching events from the database
                if (requestedAuthors.Count > 0)
                {
                    Task.Run(async () =>
                    {
                        await SendMatchingEvents(socketId, subscriptionId, requestedAuthors, requestedKinds);
                        SendEoseMessage(socketId, subscriptionId);
                    });
                }
                else
                {
                    // If no authors requested, simply send EOSE
                    SendEoseMessage(socketId, subscriptionId);
                }

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
            _logger.LogWarning(ex, "Failed to parse message as Nostr protocol from {SocketId}: {MessageLength} bytes, Error: {Error}",
                socketId, message.Length, ex.Message);

            responseMessage = CreateNostrErrorResponse($"Invalid JSON: {ex.Message}");
            return true;
        }
    }

    private async Task SendMatchingEvents(string socketId, string subscriptionId, HashSet<string> authors, List<int> kinds)
    {
        if (!_sockets.TryGetValue(socketId, out var webSocket) ||
            webSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot send events to socket {SocketId}: socket not found or not open", socketId);
            return;
        }

        // If no specific kinds requested, use all allowed kinds
        if (kinds.Count == 0)
        {
            _logger.LogDebug("No kinds specified in REQ request for subscription {SubscriptionId}. Must only be 3 and 10002", subscriptionId);
            return;
        }

        // If no specific authors requested, we can't do anything
        if (authors.Count == 0)
        {
            _logger.LogDebug("No authors specified in REQ request for subscription {SubscriptionId}", subscriptionId);
            return;
        }

        int eventsSent = 0;

        foreach (var author in authors)
        {
            foreach (var kind in kinds)
            {
                // Only query for supported kinds
                if (!_allowedEventKinds.Contains(kind))
                {
                    continue;
                }

                var eventObj = _storageService.GetEventByPubkeyAndKind(author, kind);

                if (eventObj != null)
                {
                    try
                    {
                        // Format as Nostr EVENT message: ["EVENT", subscriptionId, eventObj]
                        var eventJson = JsonSerializer.Serialize(eventObj, NostrSerializationContext.Default.NostrEvent);
                        var message = $"[\"EVENT\",\"{subscriptionId}\",{eventJson}]";
                        var messageBytes = Encoding.UTF8.GetBytes(message);

                        await webSocket.SendAsync(
                            new ArraySegment<byte>(messageBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);

                        eventsSent++;

                        _logger.LogDebug("Sent event {Id} to client {SocketId} for subscription {SubscriptionId}",
                            eventObj.Id, socketId, subscriptionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending event to client {SocketId}", socketId);
                    }
                }
            }
        }

        _logger.LogInformation("Sent {EventCount} events to client {SocketId} for subscription {SubscriptionId}",
            eventsSent, socketId, subscriptionId);
    }

    private void SendEoseMessage(string socketId, string subscriptionId)
    {
        var eoseMessage = $"[\"EOSE\",\"{subscriptionId}\"]";
        var eoseBytes = Encoding.UTF8.GetBytes(eoseMessage);

        if (_sockets.TryGetValue(socketId, out var webSocket) &&
            webSocket.State == WebSocketState.Open)
        {
            Task.Run(async () =>
            {
                try
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(eoseBytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);

                    _logger.LogDebug("Sent EOSE to client {SocketId} for subscription {SubscriptionId}",
                        socketId, subscriptionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending EOSE to {SocketId}", socketId);
                }
            });
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
