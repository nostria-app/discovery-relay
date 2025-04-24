using System.Text.Json;
using DiscoveryRelay.Models;
using Microsoft.Extensions.Logging;

namespace DiscoveryRelay.Services;

public class EventFilterService
{
    private readonly ILogger<EventFilterService> _logger;
    private readonly HashSet<int> _allowedEventKinds = new() { 3, 10002 };

    public EventFilterService(ILogger<EventFilterService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates that an event has an allowed kind
    /// </summary>
    /// <param name="nostrEvent">The event to validate</param>
    /// <returns>True if the event is allowed, otherwise false</returns>
    public bool IsEventKindAllowed(NostrEvent nostrEvent)
    {
        return _allowedEventKinds.Contains(nostrEvent.Kind);
    }

    /// <summary>
    /// Checks if a REQ's filters contain only allowed kinds
    /// </summary>
    /// <param name="jsonElement">The JSON element representing the REQ message</param>
    /// <returns>True if the filters are valid, otherwise false</returns>
    public bool AreFiltersValid(JsonElement jsonElement)
    {
        try
        {
            // REQ message should be an array with at least 3 elements: ["REQ", "subscription_id", {...filter}, ...]
            if (jsonElement.ValueKind != JsonValueKind.Array || jsonElement.GetArrayLength() < 3)
            {
                return false;
            }

            // Skip the first two elements and check each filter
            for (int i = 2; i < jsonElement.GetArrayLength(); i++)
            {
                var filter = jsonElement[i];
                
                // If the filter specifies kinds, make sure they're allowed
                if (filter.TryGetProperty("kinds", out var kindsElement) && 
                    kindsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var kindElement in kindsElement.EnumerateArray())
                    {
                        if (kindElement.TryGetInt32(out int kind) && !_allowedEventKinds.Contains(kind))
                        {
                            _logger.LogWarning("Filter contains unsupported kind: {Kind}", kind);
                            return false;
                        }
                    }
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating filters");
            return false;
        }
    }

    /// <summary>
    /// Get the list of allowed event kinds
    /// </summary>
    public IReadOnlySet<int> AllowedEventKinds => _allowedEventKinds;
}
