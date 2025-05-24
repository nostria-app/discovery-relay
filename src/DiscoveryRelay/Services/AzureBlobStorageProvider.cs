using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DiscoveryRelay.Services;

/// <summary>
/// Azure Blob Storage implementation of the storage provider
/// </summary>
public class AzureBlobStorageProvider : IStorageProvider
{
    private readonly ILogger<AzureBlobStorageProvider> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private BlobServiceClient? _blobServiceClient;
    private BlobContainerClient? _containerClient;
    private readonly string _containerName;
    private bool _disposed = false;
    private bool _isStopped = false;
    private readonly object _stopLock = new object();
    private readonly IOptions<AzureBlobOptions> _options;

    // Write statistics tracking
    private long _writeCount = 0;
    private long _lastWriteCount = 0;
    private readonly object _statsLock = new object();
    private Timer _statsTimer;
    private readonly int _statsIntervalSeconds;

    // Cache for recently accessed events (key format: "pubkey__kind")
    private readonly ConcurrentDictionary<string, NostrEvent> _eventCache = new();

    public AzureBlobStorageProvider(ILogger<AzureBlobStorageProvider> logger, IOptions<AzureBlobOptions> options)
    {
        _logger = logger;
        _options = options;
        _containerName = options.Value.ContainerName;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            TypeInfoResolver = NostrSerializationContext.Default
        };

        // Set statistics logging interval (default to 10 seconds if not specified)
        _statsIntervalSeconds = Math.Max(10, options.Value.StatsIntervalSeconds);

        Initialize();

        // Initialize statistics timer
        _statsTimer = new Timer(LogWriteStatistics, null,
            TimeSpan.FromSeconds(_statsIntervalSeconds),
            TimeSpan.FromSeconds(_statsIntervalSeconds));

        // Initialize counters
        InitializeWriteCounter();
    }

    private async void InitializeWriteCounter()
    {
        try
        {
            _writeCount = await CountBlobsAsync();
            _logger.LogInformation("Initialized write counter with {Count} blobs", _writeCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize write counter");
        }
    }

    private void LogWriteStatistics(object? state)
    {
        long currentCount;
        long previousCount;

        lock (_statsLock)
        {
            currentCount = _writeCount;
            previousCount = _lastWriteCount;
            _lastWriteCount = currentCount;
        }

        long writesPerInterval = currentCount - previousCount;
        double writesPerSecond = (double)writesPerInterval / _statsIntervalSeconds;

        _logger.LogInformation("Azure Blob Write Stats: {WritesPerInterval} writes in {IntervalSeconds} seconds ({WritesPerSecond:F2} writes/sec), Total: {TotalWrites}",
            writesPerInterval, _statsIntervalSeconds, writesPerSecond, currentCount);
    }
    private void Initialize()
    {
        try
        {
            if (_options.Value.UseManagedIdentity)
            {
                if (string.IsNullOrEmpty(_options.Value.AccountName))
                {
                    throw new ArgumentException("Azure Blob Storage account name is required when using Managed Identity");
                }

                _logger.LogInformation("Initializing Azure Blob Storage with Managed Identity for account {AccountName}", _options.Value.AccountName);

                // Create the service client using DefaultAzureCredential (which supports Managed Identity)
                var endpoint = new Uri($"https://{_options.Value.AccountName}.blob.{_options.Value.EndpointSuffix}");
                _blobServiceClient = new BlobServiceClient(endpoint, new Azure.Identity.DefaultAzureCredential());
            }
            else
            {
                if (string.IsNullOrEmpty(_options.Value.ConnectionString))
                {
                    throw new ArgumentException("Azure Blob Storage connection string is not configured");
                }

                _logger.LogInformation("Initializing Azure Blob Storage with connection string");
                _blobServiceClient = new BlobServiceClient(_options.Value.ConnectionString);
            }

            _containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);

            // Create the container if it doesn't exist
            _containerClient.CreateIfNotExists(PublicAccessType.None);

            _logger.LogInformation("Azure Blob Storage initialized with container {Container}", _containerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Azure Blob Storage");
            throw;
        }
    }

    public bool IsStopped()
    {
        lock (_stopLock)
        {
            return _isStopped;
        }
    }

    public async Task<bool> StartAsync()
    {
        lock (_stopLock)
        {
            if (!_isStopped)
            {
                _logger.LogWarning("Azure Blob Storage is already running");
                return false;
            }

            try
            {
                _logger.LogInformation("Starting Azure Blob Storage service");
                Initialize();

                // Restart the statistics timer
                _statsTimer?.Dispose();
                _statsTimer = new Timer(LogWriteStatistics, null,
                    TimeSpan.FromSeconds(_statsIntervalSeconds),
                    TimeSpan.FromSeconds(_statsIntervalSeconds));

                _isStopped = false;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting Azure Blob Storage");
                return false;
            }
        }
    }

    public async Task<bool> StopAsync()
    {
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Azure Blob Storage is already stopped");
                return false;
            }

            try
            {
                _logger.LogInformation("Stopping Azure Blob Storage service");
                _statsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _eventCache.Clear();
                _isStopped = true;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Azure Blob Storage");
                return false;
            }
        }
    }

    public async Task<bool> StoreEventAsync(NostrEvent nostrEvent)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AzureBlobStorageProvider));

        // Check if storage is stopped
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to store event while Azure Blob Storage is stopped");
                return false;
            }
        }

        // Only store events with kind 3 or 10002
        if (nostrEvent.Kind != 3 && nostrEvent.Kind != 10002)
        {
            _logger.LogWarning("Ignoring event with unsupported kind: {Kind}", nostrEvent.Kind);
            return false;
        }

        if (string.IsNullOrEmpty(nostrEvent.Id))
        {
            _logger.LogWarning("Attempted to store event with empty ID");
            return false;
        }

        if (string.IsNullOrEmpty(nostrEvent.PubKey))
        {
            _logger.LogWarning("Attempted to store event {Id} with empty PubKey", nostrEvent.Id);
            return false;
        }

        // Additional validation for created_at timestamp
        if (nostrEvent.CreatedAt <= 0)
        {
            _logger.LogWarning("Attempted to store event {Id} with invalid CreatedAt timestamp: {CreatedAt}",
                nostrEvent.Id, nostrEvent.CreatedAt);
            return false;
        }

        var pubkey = nostrEvent.PubKey;
        var kind = nostrEvent.Kind;

        try
        {
            // Create blob name using PUBKEY_KIND format
            string blobName = $"{pubkey}__{kind}";

            // If the kind is 3, check if there is already a kind 10002 event
            if (kind == 3)
            {
                string relayListBlobName = $"{pubkey}__10002";
                if (await BlobExistsAsync(relayListBlobName))
                {
                    _logger.LogDebug("Found existing event for {Pubkey} and kind 10002: Skipping saving kind 3 event.", pubkey);
                    return false;
                }
            }

            // Check if an event with this key already exists
            if (await BlobExistsAsync(blobName))
            {
                // Get the existing event
                var existingEvent = await GetEventFromBlobAsync(blobName);

                // Only update if the new event has a more recent CreatedAt timestamp
                if (existingEvent != null && nostrEvent.CreatedAt <= existingEvent.CreatedAt)
                {
                    _logger.LogDebug("Skipping store of older or same age event with ID {Id} for pubkey {Pubkey} and kind {Kind}. " +
                                    "New timestamp: {NewTimestamp}, Existing timestamp: {ExistingTimestamp}",
                                    nostrEvent.Id, pubkey, kind, nostrEvent.CreatedAt, existingEvent.CreatedAt);
                    return false;
                }

                _logger.LogDebug("Replacing older event with newer event (ID: {Id}) for pubkey {Pubkey} and kind {Kind}. " +
                               "New timestamp: {NewTimestamp}, Old timestamp: {OldTimestamp}",
                               nostrEvent.Id, pubkey, kind, nostrEvent.CreatedAt, existingEvent?.CreatedAt);
            }
            else
            {
                _logger.LogDebug("No existing event found for pubkey {Pubkey} and kind {Kind}, storing new event (ID: {Id})",
                               pubkey, kind, nostrEvent.Id);
            }

            // Serialize and store the new event
            string json = JsonSerializer.Serialize(nostrEvent, NostrSerializationContext.Default.NostrEvent);
            _logger.LogDebug("Serialized event JSON: {EventJson}", json);

            await UploadBlobAsync(blobName, json);

            // Update cache
            _eventCache[blobName] = nostrEvent;

            // Increment write counter
            lock (_statsLock)
            {
                _writeCount++;
            }

            _logger.LogDebug("Successfully stored event with ID {Id} for pubkey {Pubkey} and kind {Kind}, timestamp {Timestamp}",
                            nostrEvent.Id, pubkey, kind, nostrEvent.CreatedAt);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store event with ID {Id} for pubkey {PubKey} and kind {Kind}",
                nostrEvent.Id, nostrEvent.PubKey, nostrEvent.Kind);
            return false;
        }
    }

    public async Task<NostrEvent?> GetEventByPubkeyAndKindAsync(string pubkey, int kind)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AzureBlobStorageProvider));

        // Check if storage is stopped
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get event while Azure Blob Storage is stopped");
                return null;
            }
        }

        if (kind != 3 && kind != 10002)
        {
            _logger.LogWarning("Attempted to get event with unsupported kind: {Kind}", kind);
            return null;
        }

        try
        {
            string blobName = $"{pubkey}__{kind}";

            // Check cache first
            if (_eventCache.TryGetValue(blobName, out var cachedEvent))
            {
                return cachedEvent;
            }

            // If not in cache, get from blob storage
            var nostrEvent = await GetEventFromBlobAsync(blobName);

            // If found, add to cache
            if (nostrEvent != null)
            {
                _eventCache[blobName] = nostrEvent;
            }
            else
            {
                _logger.LogDebug("No event found for pubkey {Pubkey} and kind {Kind}", pubkey, kind);
            }

            return nostrEvent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve event for pubkey {Pubkey} and kind {Kind}", pubkey, kind);
            return null;
        }
    }

    public async Task<object?> GetEventCountAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AzureBlobStorageProvider));

        // Check if storage is stopped
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get event count while Azure Blob Storage is stopped");
                return null;
            }
        }

        try
        {
            long count = await CountBlobsAsync();
            return new { Entries = count };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event count");
            return null;
        }
    }

    public async Task<Dictionary<int, int>> GetEventCountsByKindAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AzureBlobStorageProvider));

        // Check if storage is stopped
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get event counts by kind while Azure Blob Storage is stopped");
                return new Dictionary<int, int> { { 3, 0 }, { 10002, 0 } };
            }
        }

        var counts = new Dictionary<int, int>
        {
            { 3, 0 },
            { 10002, 0 }
        };

        try
        {
            await foreach (BlobItem blobItem in _containerClient.GetBlobsAsync())
            {
                string blobName = blobItem.Name;
                string[] parts = blobName.Split("__");

                if (parts.Length == 2 && int.TryParse(parts[1], out int kind))
                {
                    if (counts.ContainsKey(kind))
                    {
                        counts[kind]++;
                    }
                }
            }

            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event counts by kind");
            return counts;
        }
    }

    public async Task<List<NostrEvent>> GetRecentEventsAsync(int limit = 10)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AzureBlobStorageProvider));

        // Check if storage is stopped
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get recent events while Azure Blob Storage is stopped");
                return new List<NostrEvent>();
            }
        }

        var events = new List<NostrEvent>();

        try
        {
            // Get all events
            var allEvents = new List<NostrEvent>();

            await foreach (BlobItem blobItem in _containerClient.GetBlobsAsync())
            {
                var nostrEvent = await GetEventFromBlobAsync(blobItem.Name);
                if (nostrEvent != null)
                {
                    allEvents.Add(nostrEvent);
                }

                // Don't retrieve too many events
                if (allEvents.Count >= limit * 2)
                {
                    break;
                }
            }

            // Sort and take the most recent ones
            events = allEvents
                .OrderByDescending(e => e.CreatedAt)
                .Take(limit)
                .ToList();

            return events;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent events");
            return events;
        }
    }

    public async Task<Dictionary<string, object>> GetStorageStatsAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AzureBlobStorageProvider));

        var stats = new Dictionary<string, object>();

        // Check if storage is stopped
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Azure Blob Storage statistics requested while storage is stopped");
                stats["status"] = "stopped";
                stats["containerName"] = _containerName;
                return stats;
            }
        }

        try
        {
            stats["status"] = "running";

            var eventCount = await GetEventCountAsync();
            if (eventCount != null)
            {
                stats["totalEvents"] = eventCount;
            }

            stats["eventsByKind"] = await GetEventCountsByKindAsync();
            stats["recentEvents"] = await GetRecentEventsAsync(10);
            stats["containerName"] = _containerName;
            stats["provider"] = "AzureBlobStorage";

            if (_options.Value.UseManagedIdentity)
            {
                stats["authType"] = "ManagedIdentity";
                stats["accountName"] = _options.Value.AccountName;
            }
            else
            {
                stats["authType"] = "ConnectionString";
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Azure Blob Storage statistics");
            stats["error"] = ex.Message;
            return stats;
        }
    }

    #region Helper Methods
    private async Task<bool> BlobExistsAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var response = await blobClient.ExistsAsync();
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if blob {BlobName} exists", blobName);
            return false;
        }
    }

    private async Task<NostrEvent?> GetEventFromBlobAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            if (!await blobClient.ExistsAsync())
            {
                return null;
            }

            BlobDownloadResult downloadResult = await blobClient.DownloadContentAsync();
            string content = downloadResult.Content.ToString();

            return JsonSerializer.Deserialize(content, NostrSerializationContext.Default.NostrEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving event from blob {BlobName}", blobName);
            return null;
        }
    }

    private async Task UploadBlobAsync(string blobName, string content)
    {
        var blobClient = _containerClient.GetBlobClient(blobName);
        var contentBytes = Encoding.UTF8.GetBytes(content);
        using var memStream = new MemoryStream(contentBytes);
        await blobClient.UploadAsync(memStream, overwrite: true);
    }

    private async Task<long> CountBlobsAsync()
    {
        // Unfortunately there are no good way to count blobs in Azure Blob Storage without iterating through them. So for now
        // we'll not do it anymore.
        return 0;

        //long count = 0;
        //await foreach (var _ in _containerClient.GetBlobsAsync())
        //{
        //    count++;
        //}
        //return count;
    }
    #endregion

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _statsTimer?.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
