using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using LightningDB;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DiscoveryRelay.Services;

public class LmdbStorageService
{
    private readonly ILogger<LmdbStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private LightningEnvironment _env;
    private bool _disposed = false;
    private readonly string _dbPath;
    private long _mapSize = 1024L * 1024L;
    private int _maxReaders = 4096;
    private const string EventsDbName = "events";
    private readonly IOptions<LmdbOptions> _options;
    private bool _isStopped = false;
    private readonly object _stopLock = new object();

    // Write statistics tracking
    private long _writeCount = 0;
    private long _lastWriteCount = 0;
    private readonly object _statsLock = new object();
    private Timer _statsTimer;
    private readonly int _statsIntervalSeconds;

    public LmdbStorageService(ILogger<LmdbStorageService> logger, IOptions<LmdbOptions> options)
    {
        _logger = logger;
        _options = options;
        _dbPath = options.Value.DatabasePath;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            TypeInfoResolver = NostrSerializationContext.Default
        };

        if (options.Value.SizeInMb > 0)
        {
            _mapSize = options.Value.SizeInMb * _mapSize;
        }

        if (options.Value.MaxReaders > 0)
        {
            _maxReaders = options.Value.MaxReaders;
        }

        // Set statistics logging interval (default to 10 seconds if not specified)
        _statsIntervalSeconds = Math.Max(10, options.Value.StatsIntervalSeconds);

        Initialize();

        // Count existing records and update _writeCount
        InitializeWriteCounter();

        // Initialize statistics timer
        _statsTimer = new Timer(LogWriteStatistics, null,
            TimeSpan.FromSeconds(_statsIntervalSeconds),
            TimeSpan.FromSeconds(_statsIntervalSeconds));
    }

    private void InitializeWriteCounter()
    {
        try
        {
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);
            using var cursor = tx.CreateCursor(db);
            _writeCount = db.DatabaseStats.Entries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize write counter from existing records");
            // If counting fails, keep counters at zero
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

        _logger.LogInformation("LMDB Write Stats: {WritesPerInterval} writes in {IntervalSeconds} seconds ({WritesPerSecond:F2} writes/sec), Total: {TotalWrites}",
            writesPerInterval, _statsIntervalSeconds, writesPerSecond, currentCount);
    }

    private void Initialize()
    {
        try
        {
            // Ensure directory exists
            if (!Directory.Exists(_dbPath))
            {
                Directory.CreateDirectory(_dbPath);
                _logger.LogInformation("Created LMDB directory at {Path}", _dbPath);
            }

            // Initialize LMDB environment
            _env = new LightningEnvironment(_dbPath)
            {
                MapSize = _mapSize,
                MaxDatabases = 1,
                MaxReaders = _maxReaders,
            };

            _env.Open(EnvironmentOpenFlags.NoSync);

            // Create database if it doesn't exist
            using (var tx = _env.BeginTransaction())
            {
                tx.OpenDatabase(EventsDbName, new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
                tx.Commit();
            }

            _logger.LogInformation("LMDB environment initialized at {Path} with {MapSize}MB and stats interval of {StatsInterval}s",
                _dbPath, _mapSize / (1024L * 1024L), _statsIntervalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LMDB environment at {Path}", _dbPath);
            throw;
        }
    }

    /// <summary>
    /// Stops the database service. Used when backing up the database files.
    /// </summary>
    public bool StopDatabase()
    {
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Database is already stopped");
                return false;
            }

            try
            {
                _logger.LogInformation("Stopping LMDB database service");
                _statsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _env?.Dispose();
                _env = null;
                _isStopped = true;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping LMDB database");
                return false;
            }
        }
    }

    /// <summary>
    /// Starts the database service. Used after backing up the database files.
    /// </summary>
    public bool StartDatabase()
    {
        lock (_stopLock)
        {
            if (!_isStopped)
            {
                _logger.LogWarning("Database is already running");
                return false;
            }

            try
            {
                _logger.LogInformation("Starting LMDB database service");
                Initialize();
                InitializeWriteCounter();

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
                _logger.LogError(ex, "Error starting LMDB database");
                return false;
            }
        }
    }

    /// <summary>
    /// Checks if the database service is currently stopped
    /// </summary>
    public bool IsDatabaseStopped()
    {
        lock (_stopLock)
        {
            return _isStopped;
        }
    }

    /// <summary>
    /// Stores a Nostr event in the LMDB database, but only if it's newer than any existing event for the same pubkey and kind
    /// </summary>
    public bool StoreEvent(NostrEvent nostrEvent)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        // Check if database is stopped and return immediately if it is
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to store event while database is stopped");
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

        try
        {
            using var tx = _env.BeginTransaction();
            using var eventsDb = tx.OpenDatabase(EventsDbName);

            var pubkey = nostrEvent.PubKey;
            var kind = nostrEvent.Kind;

            // Create key in format PUBKEY__KIND
            var dbKey = $"{pubkey}__{kind}";
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(dbKey);

            // If the kind is 3, we will first validate if there is already a kind 10002 event. If there is, we will not store the kind 3 event.
            if (kind == 3)
            {
                var dbKeyRelayList = $"{pubkey}__10002";
                var keyBytesRelayList = System.Text.Encoding.UTF8.GetBytes(dbKey);

                if (tx.TryGet(eventsDb, keyBytesRelayList, out var _))
                {
                    // There is already a kind 10002 event, so we will not store the kind 3 event.
                    _logger.LogDebug("Found existing event for {Pubkey} and kind 3: Skipping saving kind 10002.", pubkey);
                    return false;
                }
            }

            // Check if an event with this key already exists
            if (tx.TryGet(eventsDb, keyBytes, out var existingValueBytes))
            {
                // Convert existing value to NostrEvent
                var existingJson = System.Text.Encoding.UTF8.GetString(existingValueBytes);
                _logger.LogDebug("Found existing event for {Pubkey} and kind {Kind}: {ExistingJson}", pubkey, kind, existingJson);

                var existingEvent = JsonSerializer.Deserialize(existingJson, NostrSerializationContext.Default.NostrEvent);

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
            var value = JsonSerializer.Serialize(nostrEvent, NostrSerializationContext.Default.NostrEvent);
            _logger.LogDebug("Serialized event JSON: {EventJson}", value);

            var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);

            // Store the event with a single put operation
            tx.Put(eventsDb, keyBytes, valueBytes);

            tx.Commit();

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

    /// <summary>
    /// Gets a Nostr event for a specific pubkey and kind (3 or 10002)
    /// </summary>
    public NostrEvent? GetEventByPubkeyAndKind(string pubkey, int kind)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        // Check if database is stopped and return immediately if it is
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get event while database is stopped");
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
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);

            // Look up using pubkey__kind format
            var dbKey = $"{pubkey}__{kind}";
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(dbKey);

            if (tx.TryGet(db, keyBytes, out var valueBytes))
            {
                var json = System.Text.Encoding.UTF8.GetString(valueBytes);
                var nostrEvent = JsonSerializer.Deserialize(json, NostrSerializationContext.Default.NostrEvent);

                return nostrEvent;
            }

            _logger.LogDebug("No event found for pubkey {Pubkey} and kind {Kind}", pubkey, kind);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve event for pubkey {Pubkey} and kind {Kind}", pubkey, kind);
            return null;
        }
    }

    /// <summary>
    /// Gets the total count of events in the database
    /// </summary>
    public Stats GetEventCount()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        // Check if database is stopped and return immediately if it is
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get event count while database is stopped");
                return null;
            }
        }

        try
        {
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);

            // Use database statistics to get count
            var stat = db.DatabaseStats;
            return stat;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event count");
            return null;
        }
    }

    /// <summary>
    /// Gets counts per event kind
    /// </summary>
    public Dictionary<int, int> GetEventCountsByKind()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        // Check if database is stopped and return immediately if it is
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get event counts by kind while database is stopped");
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
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);

            using var cursor = tx.CreateCursor(db);

            var count = 0;

            // Iterate through all key-value pairs
            var result = cursor.Count(out count);

            counts[0] = count;

            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event counts by kind");
            return counts;
        }
    }

    /// <summary>
    /// Gets the most recent events from the database
    /// </summary>
    public List<NostrEvent> GetRecentEvents(int limit = 10)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        // Check if database is stopped and return immediately if it is
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Attempted to get recent events while database is stopped");
                return new List<NostrEvent>();
            }
        }

        var events = new List<NostrEvent>();

        try
        {
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);

            // Get all events first
            var allEvents = new List<NostrEvent>();
            using var cursor = tx.CreateCursor(db);

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

    /// <summary>
    /// Gets database statistics
    /// </summary>
    public Dictionary<string, object> GetDatabaseStats()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        var stats = new Dictionary<string, object>();

        // Check if database is stopped and return minimal stats if it is
        lock (_stopLock)
        {
            if (_isStopped)
            {
                _logger.LogWarning("Database statistics requested while database is stopped");
                stats["status"] = "stopped";
                stats["databasePath"] = _dbPath;
                return stats;
            }
        }

        try
        {
            stats["status"] = "running";
            stats["totalEvents"] = GetEventCount();
            stats["eventsByKind"] = GetEventCountsByKind();
            stats["recentEvents"] = GetRecentEvents(10);

            // Get environment stats
            var envInfo = _env.EnvironmentStats;
            stats["databasePath"] = _dbPath;

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get database statistics");
            stats["error"] = ex.Message;
            return stats;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _statsTimer?.Dispose();
                _env?.Dispose();
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
