using System.Text.Json.Serialization;
//using DiscoveryRelay.Controllers;

namespace DiscoveryRelay.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NostrMessage))]
[JsonSerializable(typeof(NostrReqMessage))]
[JsonSerializable(typeof(NostrCloseMessage))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(BroadcastRequest))]
[JsonSerializable(typeof(NostrRelayInfo))]
[JsonSerializable(typeof(NostrEvent))]
[JsonSerializable(typeof(List<List<string>>))]
[JsonSerializable(typeof(DidNostrDocument))]
[JsonSerializable(typeof(VerificationMethod))]
[JsonSerializable(typeof(Service))]
// Add API response model types for source generation
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(ConnectionInfo))]
[JsonSerializable(typeof(BroadcastResponse))]
[JsonSerializable(typeof(ErrorResponse))]
// Add Todo serialization support
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(Todo[]))]
// Add needed types for dynamic compilation
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<int, int>))]
[JsonSerializable(typeof(List<NostrEvent>))]
internal partial class NostrSerializationContext : JsonSerializerContext
{
    // Static method to create default JSON options that can be used throughout the app
    public static System.Text.Json.JsonSerializerOptions GetDefaultOptions()
    {
        return new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}

// Model classes for API responses
public record VersionResponse(string Version);
public record StatusResponse(string Status, DateTime Timestamp);
public record ConnectionInfo(int ActiveConnections, int TotalSubscriptions);
public record StatsResponse(DateTime Timestamp, ConnectionInfo Connections, object DatabaseStats);
public record BroadcastResponse(bool Success);
public record ErrorResponse(string Error);
public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);
