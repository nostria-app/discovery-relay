using System.Text.Json.Serialization;
using DiscoveryRelay.Controllers;

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
internal partial class NostrSerializationContext : JsonSerializerContext
{
}
