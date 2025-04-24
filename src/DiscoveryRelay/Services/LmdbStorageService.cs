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
                MapSize = 1024L * 1024L * 1024L * 10L, // 10 GB
                MaxDatabases = 5,
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
    /// Stores a Nostr event in the LMDB database
    /// </summary>
    public bool StoreEvent(NostrEvent nostrEvent)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));
        
        if (string.IsNullOrEmpty(nostrEvent.Id))
        {
            _logger.LogWarning("Attempted to store event with empty ID");
            return false;
        }
        
        try
        {
            using var tx = _env.BeginTransaction();
            using var db = tx.OpenDatabase(EventsDbName);
            
            var key = nostrEvent.Id;
            var value = JsonSerializer.Serialize(nostrEvent, NostrSerializationContext.Default.NostrEvent);
            
            // Convert to byte arrays
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
            
            // Put in database, overwriting if exists
            tx.Put(db, keyBytes, valueBytes, PutOptions.NoOverwrite);
            tx.Commit();
            
            _logger.LogDebug("Stored event with ID {Id}, kind {Kind}", nostrEvent.Id, nostrEvent.Kind);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store event with ID {Id}", nostrEvent.Id);
            return false;
        }
    }
    
    /// <summary>
    /// Retrieves a Nostr event from the LMDB database by ID
    /// </summary>
    public NostrEvent? GetEvent(string eventId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LmdbStorageService));
        
        try
        {
            using var tx = _env.BeginTransaction(TransactionBeginFlags.ReadOnly);
            using var db = tx.OpenDatabase(EventsDbName);
            
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(eventId);
            
            if (tx.TryGet(db, keyBytes, out var valueBytes))
            {
                var json = System.Text.Encoding.UTF8.GetString(valueBytes);
                var nostrEvent = JsonSerializer.Deserialize(json, NostrSerializationContext.Default.NostrEvent);
                
                return nostrEvent;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve event with ID {Id}", eventId);
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
