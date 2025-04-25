using System.Text.Json.Serialization;
//using DiscoveryRelay.Controllers;

namespace DiscoveryRelay.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
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
internal partial class NostrSerializationContext : JsonSerializerContext
{
}

// Model classes for API responses
public record VersionResponse(string Version);
public record StatusResponse(string Status, DateTime Timestamp);
public record ConnectionInfo(int ActiveConnections, int TotalSubscriptions);
public record StatsResponse(DateTime Timestamp, ConnectionInfo Connections, Dictionary<string, object> Database);
public record BroadcastResponse(bool Success);
public record ErrorResponse(string Error);
