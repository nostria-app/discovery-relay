using System.Text.Json.Serialization;

namespace DiscoveryRelay.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class NostrEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("pubkey")]
    public string PubKey { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("kind")]
    public int Kind { get; set; }

    [JsonPropertyName("tags")]
    public List<List<string>> Tags { get; set; } = new List<List<string>>();

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("sig")]
    public string Signature { get; set; } = string.Empty;
    
    /// <summary>
    /// Calculates the event ID (sha256 hash of the serialized event data)
    /// </summary>
    public string CalculateId()
    {
        // Implementation would go here to calculate the event ID
        // This is a placeholder
        return Id;
    }
    
    /// <summary>
    /// Verifies that the event signature is valid
    /// </summary>
    public string? VerifySignature()
    {
        return Utils.SignatureValidator.Validate(this);
    }
}
