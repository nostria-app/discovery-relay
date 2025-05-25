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
    private readonly ConcurrentDictionary<string, DateTime> _lastMeaningfulActivity = new();
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Timer _statsTimer;
    private readonly Timer _idleConnectionTimer;
    private readonly RelayOptions _options;
    private readonly IStorageProvider _storageService;
    private readonly CancellationTokenSource _shutdownTokenSource = new CancellationTokenSource();
    private readonly int _maxMessageLength;

    private readonly HashSet<int> _allowedEventKinds = new() { 3, 10002 };

    public WebSocketHandler(
        ILogger<WebSocketHandler> logger,
        IOptions<RelayOptions> options,
        IStorageProvider storageService)
    {
        _logger = logger;
        _options = options.Value;
        _storageService = storageService;

        _maxMessageLength = _options.Limitations?.MaxMessageLength ?? 64 * 1024;
        _logger.LogInformation("WebSocket max message length configured to {MaxMessageLength} bytes", _maxMessageLength);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = NostrSerializationContext.Default
        };

        var statsInterval = TimeSpan.FromMinutes(_options.StatsLogIntervalMinutes);
        _statsTimer = new Timer(LogConnectionStats, null, statsInterval, statsInterval);

        _idleConnectionTimer = new Timer(CheckIdleConnections, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void LogConnectionStats(object? state)
    {
        var connectionCount = _sockets.Count;
        var totalSubscriptions = _clientSubscriptions.Values.Sum(x => x.Count);

        _logger.LogInformation("Active connections: {ConnectionCount}, Total subscriptions: {SubscriptionCount}",
            connectionCount, totalSubscriptions);
    }

    public int GetActiveConnectionCount()
    {
        return _sockets.Count;
    }

    public int GetTotalSubscriptionCount()
    {
        return _clientSubscriptions.Values.Sum(x => x.Count);
    }

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
            if (_lastMeaningfulActivity.TryGetValue(socketId, out var lastMeaningfulActivity))
            {
                if (now - lastMeaningfulActivity <= idleTimeout)
                {
                    _logger.LogDebug("Client {SocketId} has meaningful activity, skipping idle check", socketId);
                    continue;
                }
            }

            if (now - lastActivity > idleTimeout)
            {
                if (_sockets.TryGetValue(socketId, out var webSocket) &&
                    webSocket.State == WebSocketState.Open)
                {
                    _logger.LogInformation("Disconnecting idle client {SocketId} (last activity: {LastActivity}, last meaningful activity: {LastMeaningfulActivity})",
                        socketId, lastActivity, _lastMeaningfulActivity.TryGetValue(socketId, out var lma) ? lma.ToString() : "none");

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
                    _lastActivityTime.TryRemove(socketId, out _);
                    _lastMeaningfulActivity.TryRemove(socketId, out _);
                }
            }
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("WebSocketHandler is being disposed");

        _shutdownTokenSource.Cancel();

        _statsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _idleConnectionTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        CloseAllSockets().GetAwaiter().GetResult();

        _statsTimer?.Dispose();
        _idleConnectionTimer?.Dispose();
        _shutdownTokenSource.Dispose();

        _sockets.Clear();
        _clientSubscriptions.Clear();
        _lastActivityTime.Clear();
        _lastMeaningfulActivity.Clear();

        _logger.LogInformation("WebSocketHandler disposed successfully");
    }

    private async Task CloseAllSockets()
    {
        if (_sockets.IsEmpty)
        {
            return;
        }

        _logger.LogInformation("Closing {Count} WebSocket connections due to shutdown", _sockets.Count);

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

                    var timeoutTask = Task.Delay(1000);

                    closeTasks.Add(Task.WhenAny(closeTask, timeoutTask));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket {SocketId} during shutdown", socketId);
            }
        }

        if (closeTasks.Count > 0)
        {
            await Task.WhenAll(closeTasks);
        }

        _sockets.Clear();
        _clientSubscriptions.Clear();
        _lastActivityTime.Clear();
        _lastMeaningfulActivity.Clear();

        _logger.LogInformation("All WebSocket connections closed");
    }

    public async Task HandleWebSocketAsync(HttpContext context, WebSocket webSocket)
    {
        var socketId = Guid.NewGuid().ToString();
        _sockets.TryAdd(socketId, webSocket);
        _clientSubscriptions.TryAdd(socketId, new HashSet<string>());

        _lastActivityTime[socketId] = DateTime.UtcNow;
        _lastMeaningfulActivity[socketId] = DateTime.UtcNow;

        _logger.LogInformation("WebSocket connected: {SocketId}", socketId);

        try
        {
            await ProcessWebSocketAsync(socketId, webSocket, _shutdownTokenSource.Token);
        }
        catch (OperationCanceledException) when (_shutdownTokenSource.IsCancellationRequested)
        {
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
        var bufferSize = Math.Min(Math.Max(_maxMessageLength, 16 * 1024), 4 * 1024 * 1024);
        var buffer = new byte[Math.Min(bufferSize, 64 * 1024)];
        WebSocketReceiveResult receiveResult;

        using var messageBuffer = new MemoryStream(bufferSize);

        try
        {
            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);

            while (!receiveResult.CloseStatus.HasValue)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _lastActivityTime[socketId] = DateTime.UtcNow;

                    if (receiveResult.Count > 0)
                    {
                        _lastMeaningfulActivity[socketId] = DateTime.UtcNow;
                    }

                    if (messageBuffer.Length + receiveResult.Count > _maxMessageLength)
                    {
                        _logger.LogWarning("Message from {SocketId} exceeds maximum allowed size of {MaxSize} bytes",
                            socketId, _maxMessageLength);

                        var errorMessage = CreateNostrErrorResponse($"Message too large. Maximum allowed size is {_maxMessageLength} bytes");
                        var errorBytes = Encoding.UTF8.GetBytes(errorMessage);

                        await webSocket.SendAsync(
                            new ArraySegment<byte>(errorBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);

                        messageBuffer.SetLength(0);

                        if (!receiveResult.EndOfMessage)
                        {
                            do
                            {
                                receiveResult = await webSocket.ReceiveAsync(
                                    new ArraySegment<byte>(buffer), cancellationToken);
                            } while (!receiveResult.EndOfMessage && !cancellationToken.IsCancellationRequested);
                        }

                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), cancellationToken);
                        continue;
                    }

                    messageBuffer.Write(buffer, 0, receiveResult.Count);

                    if (!receiveResult.EndOfMessage)
                    {
                        _logger.LogDebug("Received partial message from {SocketId}, continuing to next frame", socketId);
                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), cancellationToken);
                        continue;
                    }

                    var messageBytes = messageBuffer.ToArray();
                    var receivedMessage = Encoding.UTF8.GetString(messageBytes, 0, messageBytes.Length);

                    _lastMeaningfulActivity[socketId] = DateTime.UtcNow;
                    _logger.LogDebug("Message received from {SocketId} (size: {Size} bytes): {MessagePreview}...",
                        socketId, messageBytes.Length,
                        receivedMessage.Length > 100 ? receivedMessage.Substring(0, 100) : receivedMessage);

                    messageBuffer.SetLength(0);

                    var (handled, responseMessage) = await TryParseNostrMessageAsync(socketId, receivedMessage);
                    if (handled)
                    {
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
                    _logger.LogDebug("Processing WebSocket {SocketId} canceled", socketId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message from {SocketId}: {Message}", socketId, ex.Message);

                    var errorMessage = $"Error processing message: {ex.Message}";
                    var errorBytes = Encoding.UTF8.GetBytes(errorMessage);

                    try
                    {
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(errorBytes, 0, errorBytes.Length),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                    catch
                    {
                        // Ignore errors when trying to send error messages
                    }

                    messageBuffer.SetLength(0);
                }

                try
                {
                    receiveResult = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("WebSocket {SocketId} was closed prematurely by the client", socketId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("WebSocket {SocketId} processing canceled due to shutdown", socketId);
        }
    }

    private async Task<(bool Handled, string? ResponseMessage)> TryParseNostrMessageAsync(string socketId, string message)
    {
        string? responseMessage = null;

        try
        {
            if (message.Length > _maxMessageLength)
            {
                _logger.LogWarning("Message from {SocketId} exceeds maximum size ({Size} bytes)",
                    socketId, message.Length);
                responseMessage = CreateNostrErrorResponse($"Message too large. Maximum allowed size is {_maxMessageLength} bytes");
                return (true, responseMessage);
            }

            var jsonDocument = JsonDocument.Parse(message);

            if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array ||
                jsonDocument.RootElement.GetArrayLength() < 2)
            {
                return (false, null);
            }

            var messageType = jsonDocument.RootElement[0].GetString();

            if (messageType == "EVENT" && jsonDocument.RootElement.GetArrayLength() >= 2)
            {
                try
                {
                    _lastMeaningfulActivity[socketId] = DateTime.UtcNow;

                    var eventJson = jsonDocument.RootElement[1].GetRawText();
                    _logger.LogDebug("Raw event JSON: {EventJson}", eventJson);

                    // Replace with source-generated deserialization
                    var nostrEvent = JsonSerializer.Deserialize<NostrEvent>(eventJson, NostrSerializationContext.Default.NostrEvent);

                    if (nostrEvent == null)
                    {
                        _logger.LogWarning("EVENT deserialization failed - returned null object");
                        responseMessage = CreateNostrErrorResponse("Invalid event format");
                        return (true, responseMessage);
                    }

                    _logger.LogDebug("Received EVENT from {SocketId}, kind: {Kind}, id: {Id}, pubkey: {PubKey}, created_at: {CreatedAt}, signature length: {SigLength}",
                        socketId, nostrEvent.Kind, nostrEvent.Id, nostrEvent.PubKey, nostrEvent.CreatedAt, nostrEvent.Signature?.Length ?? 0);

                    if (string.IsNullOrEmpty(nostrEvent.Id))
                    {
                        _logger.LogWarning("Rejected event: missing ID field");
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing ID field");
                        return (true, responseMessage);
                    }

                    if (string.IsNullOrEmpty(nostrEvent.PubKey))
                    {
                        _logger.LogWarning("Rejected event {Id}: missing PubKey field", nostrEvent.Id);
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing PubKey field");
                        return (true, responseMessage);
                    }

                    if (string.IsNullOrEmpty(nostrEvent.Signature))
                    {
                        _logger.LogWarning("Rejected event {Id}: missing Signature field", nostrEvent.Id);
                        responseMessage = CreateNostrErrorResponse("Invalid event: missing Signature field");
                        return (true, responseMessage);
                    }

                    if (!_allowedEventKinds.Contains(nostrEvent.Kind))
                    {
                        _logger.LogWarning("Rejected event {Id} with unsupported kind: {Kind}", nostrEvent.Id, nostrEvent.Kind);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"restricted: only kinds 3 and 10002 are accepted\"]";
                        return (true, responseMessage);
                    }

                    if (nostrEvent.CreatedAt <= 0)
                    {
                        _logger.LogWarning("Rejected event {Id}: invalid or missing CreatedAt timestamp: {CreatedAt}",
                            nostrEvent.Id, nostrEvent.CreatedAt);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"invalid: created_at timestamp is invalid\"]";
                        return (true, responseMessage);
                    }

                    string? validationError = nostrEvent.VerifySignature();
                    if (validationError != null)
                    {
                        _logger.LogWarning("Rejected event {Id}: signature validation failed: {Error}",
                            nostrEvent.Id, validationError);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"invalid: {validationError}\"]";
                        return (true, responseMessage);
                    }

                    string calculatedId = nostrEvent.CalculateId();
                    if (calculatedId != nostrEvent.Id)
                    {
                        _logger.LogWarning("Rejected event: ID mismatch. Provided: {ProvidedId}, Calculated: {CalculatedId}",
                            nostrEvent.Id, calculatedId);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"invalid: event id does not match calculated id\"]";
                        return (true, responseMessage);
                    }

                    _logger.LogDebug("Attempting to store event {Id} in storage", nostrEvent.Id);
                    bool stored = await _storageService.StoreEventAsync(nostrEvent);

                    if (!stored)
                    {
                        _logger.LogDebug("Failed to store event {Id} in storage - storage service returned false", nostrEvent.Id);
                        responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",false,\"error: failed to store event\"]";
                        return (true, responseMessage);
                    }

                    _logger.LogDebug("Event {Id} successfully stored in storage", nostrEvent.Id);

                    responseMessage = $"[\"OK\",\"{nostrEvent.Id}\",true,\"\"]";

                    return (true, responseMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing EVENT message");
                    responseMessage = CreateNostrErrorResponse($"Error processing event: {ex.Message}");
                    return (true, responseMessage);
                }
            }
            else if (messageType == "REQ" && jsonDocument.RootElement.GetArrayLength() >= 3)
            {
                _lastMeaningfulActivity[socketId] = DateTime.UtcNow;

                var subscriptionId = jsonDocument.RootElement[1].GetString() ?? string.Empty;

                bool hasInvalidKinds = false;

                List<int> requestedKinds = new();
                HashSet<string> requestedAuthors = new();

                for (int i = 2; i < jsonDocument.RootElement.GetArrayLength(); i++)
                {
                    var filterElement = jsonDocument.RootElement[i];

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
                    return (true, responseMessage);
                }

                if (_clientSubscriptions.TryGetValue(socketId, out var subscriptions))
                {
                    if (!subscriptions.Contains(subscriptionId))
                    {
                        subscriptions.Add(subscriptionId);
                        _logger.LogInformation("Client {SocketId} added subscription {SubscriptionId}, total subscriptions: {Count}",
                            socketId, subscriptionId, subscriptions.Count);
                    }
                }

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
                    SendEoseMessage(socketId, subscriptionId);
                }

                return (true, null);
            }
            else if (messageType == "CLOSE" && jsonDocument.RootElement.GetArrayLength() >= 2)
            {
                _lastMeaningfulActivity[socketId] = DateTime.UtcNow;

                var subscriptionId = jsonDocument.RootElement[1].GetString() ?? string.Empty;

                if (_clientSubscriptions.TryGetValue(socketId, out var subscriptions))
                {
                    subscriptions.Remove(subscriptionId);
                    _logger.LogInformation("Client {SocketId} removed subscription {SubscriptionId}, remaining subscriptions: {Count}",
                        socketId, subscriptionId, subscriptions.Count);

                    if (subscriptions.Count == 0)
                    {
                        _logger.LogInformation("Client {SocketId} has no more subscriptions, will disconnect after 5 minutes if no new subscriptions are created", socketId);

                        // Store the time when the client ran out of subscriptions
                        if (!_lastMeaningfulActivity.TryGetValue(socketId, out _))
                        {
                            _lastMeaningfulActivity[socketId] = DateTime.UtcNow;
                        }

                        // Don't immediately disconnect - the idle connection check will handle this
                        // The existing CheckIdleConnections method will disconnect after DisconnectTimeoutMinutes (default 5)
                    }

                    // if (subscriptions.Count == 0)
                    // {
                    //     _logger.LogInformation("Client {SocketId} has no more subscriptions, will disconnect", socketId);
                    //     Task.Run(async () =>
                    //     {
                    //         await Task.Delay(500);
                    //         if (_sockets.TryGetValue(socketId, out var webSocket) &&
                    //             webSocket.State == WebSocketState.Open)
                    //         {
                    //             await webSocket.CloseAsync(
                    //                 WebSocketCloseStatus.NormalClosure,
                    //                 "No active subscriptions",
                    //                 CancellationToken.None);
                    //         }
                    //     });
                    // }
                }

                responseMessage = $"Subscription {subscriptionId} closed";
                return (true, responseMessage);
            }

            return (false, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse message as Nostr protocol from {SocketId}: {MessageLength} bytes, Error: {Error}",
                socketId, message.Length, ex.Message);

            responseMessage = CreateNostrErrorResponse($"Invalid JSON: {ex.Message}");
            return (true, responseMessage);
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

        if (kinds.Count == 0)
        {
            _logger.LogDebug("No kinds specified in REQ request for subscription {SubscriptionId}. Must only be 3 and 10002", subscriptionId);
            return;
        }

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
                if (!_allowedEventKinds.Contains(kind))
                {
                    continue;
                }

                var eventObj = await _storageService.GetEventByPubkeyAndKindAsync(author, kind);

                if (eventObj != null)
                {
                    try
                    {
                        var eventJson = JsonSerializer.Serialize(eventObj, NostrSerializationContext.Default.NostrEvent);
                        var message = $"[\"EVENT\",\"{subscriptionId}\",{eventJson}]";
                        var messageBytes = Encoding.UTF8.GetBytes(message);

                        await webSocket.SendAsync(
                            new ArraySegment<byte>(messageBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);

                        _lastMeaningfulActivity[socketId] = DateTime.UtcNow;

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

                    _lastMeaningfulActivity[socketId] = DateTime.UtcNow;

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
        return $"[\"NOTICE\",\"{errorMessage}\"]";
    }

    private async Task CloseWebSocketAsync(string socketId, WebSocket webSocket, string reason = "Closing")
    {
        if (_sockets.TryRemove(socketId, out _))
        {
            _clientSubscriptions.TryRemove(socketId, out _);
            _lastActivityTime.TryRemove(socketId, out _);
            _lastMeaningfulActivity.TryRemove(socketId, out _);
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

                    _lastMeaningfulActivity[socket.Key] = DateTime.UtcNow;
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
