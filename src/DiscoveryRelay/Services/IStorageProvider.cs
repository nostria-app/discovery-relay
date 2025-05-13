using DiscoveryRelay.Models;
using LightningDB;

namespace DiscoveryRelay.Services;

/// <summary>
/// Interface for a storage provider that can store and retrieve Nostr events
/// </summary>
public interface IStorageProvider : IDisposable
{
    /// <summary>
    /// Stores a Nostr event in the storage, but only if it's newer than any existing event for the same pubkey and kind
    /// </summary>
    Task<bool> StoreEventAsync(NostrEvent nostrEvent);

    /// <summary>
    /// Gets a Nostr event for a specific pubkey and kind (3 or 10002)
    /// </summary>
    Task<NostrEvent?> GetEventByPubkeyAndKindAsync(string pubkey, int kind);

    /// <summary>
    /// Gets the total count of events in the storage
    /// </summary>
    Task<object?> GetEventCountAsync();

    /// <summary>
    /// Gets counts per event kind
    /// </summary>
    Task<Dictionary<int, int>> GetEventCountsByKindAsync();

    /// <summary>
    /// Gets the most recent events from the storage
    /// </summary>
    Task<List<NostrEvent>> GetRecentEventsAsync(int limit = 10);

    /// <summary>
    /// Gets storage statistics
    /// </summary>
    Task<Dictionary<string, object>> GetStorageStatsAsync();

    /// <summary>
    /// Stops the storage service.
    /// </summary>
    Task<bool> StopAsync();

    /// <summary>
    /// Starts the storage service.
    /// </summary>
    Task<bool> StartAsync();

    /// <summary>
    /// Checks if the storage service is currently stopped
    /// </summary>
    bool IsStopped();
}
