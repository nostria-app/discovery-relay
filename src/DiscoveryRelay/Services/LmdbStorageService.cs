using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using LightningDB;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DiscoveryRelay.Services;

public class LmdbStorageService : IDisposable
{
    private readonly ILogger<LmdbStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private LightningEnvironment _env;
    private bool _disposed = false;
    private readonly string _dbPath;

    private const string EventsDbName = "events";

    public LmdbStorageService(ILogger<LmdbStorageService> logger, IOptions<LmdbOptions> options)
    {
        _logger = logger;
        _dbPath = options.Value.DatabasePath;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            TypeInfoResolver = NostrSerializationContext.Default
        };

        Initialize();
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
                MapSize = 1024L * 1024L * 100L, // 100 MB
                MaxDatabases = 2,
                MaxReaders = 4096,
            };

            _env.Open(EnvironmentOpenFlags.NoSync);

            // Create database if it doesn't exist
            using (var tx = _env.BeginTransaction())
            {
                tx.OpenDatabase(EventsDbName, new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
                tx.Commit();
            }

            _logger.LogInformation("LMDB environment initialized at {Path}", _dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LMDB environment at {Path}", _dbPath);
            throw;
        }
    }

    /// <summary>
    /// Stores a Nostr event in the LMDB database, but only if it's newer than any existing event for the same pubkey and kind
    /// </summary>
    public bool StoreEvent(NostrEvent nostrEvent)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        // Only store events with kind 3 or 10002
        if (nostrEvent.Kind != 3 && nostrEvent.Kind != 10002)
        {
            _logger.LogWarning("Ignoring event with unsupported kind: {Kind}", nostrEvent.Kind);
            return false;
        }

        if (string.IsNullOrEmpty(nostrEvent.Id) || string.IsNullOrEmpty(nostrEvent.PubKey))
        {
            _logger.LogWarning("Attempted to store event with empty ID or PubKey");
            return false;
        }

        try
        {
            using var tx = _env.BeginTransaction();
            using var eventsDb = tx.OpenDatabase(EventsDbName);

            var pubkey = nostrEvent.PubKey;
            var kind = nostrEvent.Kind;

            // Create key in format PUBKEY::KIND
            var dbKey = $"{pubkey}::{kind}";
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(dbKey);

            // Check if an event with this key already exists
            if (tx.TryGet(eventsDb, keyBytes, out var existingValueBytes))
            {
                // Convert existing value to NostrEvent
                var existingJson = System.Text.Encoding.UTF8.GetString(existingValueBytes);
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
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);

            // Store the event with a single put operation
            tx.Put(eventsDb, keyBytes, valueBytes);

            tx.Commit();

            _logger.LogDebug("Stored event with ID {Id} for pubkey {Pubkey} and kind {Kind}, timestamp {Timestamp}",
                            nostrEvent.Id, pubkey, kind, nostrEvent.CreatedAt);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store event with ID {Id}", nostrEvent.Id);
            return false;
        }
    }

    /// <summary>
    /// Gets a Nostr event for a specific pubkey and kind (3 or 10002)
    /// </summary>
    public NostrEvent? GetEventByPubkeyAndKind(string pubkey, int kind)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));

        if (kind != 3 && kind != 10002)
        {
            _logger.LogWarning("Attempted to get event with unsupported kind: {Kind}", kind);
            return null;
        }

        try
        {
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);

            // Look up using pubkey::kind format
            var dbKey = $"{pubkey}::{kind}";
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

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
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
