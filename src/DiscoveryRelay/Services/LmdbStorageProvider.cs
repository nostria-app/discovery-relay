using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using LightningDB;
using Microsoft.Extensions.Options;

namespace DiscoveryRelay.Services;

/// <summary>
/// LMDB implementation of the storage provider that wraps the existing LmdbStorageService
/// </summary>
public class LmdbStorageProvider : IStorageProvider
{
    private readonly LmdbStorageService _lmdbService;
    private readonly ILogger<LmdbStorageProvider> _logger;

    public LmdbStorageProvider(ILogger<LmdbStorageProvider> logger, LmdbStorageService lmdbService)
    {
        _logger = logger;
        _lmdbService = lmdbService;
    }

    public void Dispose()
    {
        _lmdbService.Dispose();
    }

    public Task<object?> GetEventCountAsync()
    {
        var result = _lmdbService.GetEventCount();
        return Task.FromResult<object?>(result);
    }

    public Task<Dictionary<int, int>> GetEventCountsByKindAsync()
    {
        var result = _lmdbService.GetEventCountsByKind();
        return Task.FromResult(result);
    }

    public Task<NostrEvent?> GetEventByPubkeyAndKindAsync(string pubkey, int kind)
    {
        var result = _lmdbService.GetEventByPubkeyAndKind(pubkey, kind);
        return Task.FromResult(result);
    }

    public Task<List<NostrEvent>> GetRecentEventsAsync(int limit = 10)
    {
        var result = _lmdbService.GetRecentEvents(limit);
        return Task.FromResult(result);
    }

    public Task<Dictionary<string, object>> GetStorageStatsAsync()
    {
        var result = _lmdbService.GetDatabaseStats();
        return Task.FromResult(result);
    }

    public bool IsStopped()
    {
        return _lmdbService.IsDatabaseStopped();
    }

    public Task<bool> StartAsync()
    {
        var result = _lmdbService.StartDatabase();
        return Task.FromResult(result);
    }

    public Task<bool> StopAsync()
    {
        var result = _lmdbService.StopDatabase();
        return Task.FromResult(result);
    }

    public Task<bool> StoreEventAsync(NostrEvent nostrEvent)
    {
        var result = _lmdbService.StoreEvent(nostrEvent);
        return Task.FromResult(result);
    }
}
