using System.Text.Json.Serialization;

namespace DiscoveryRelay.Models;

/// <summary>
/// Represents the relay information document according to Nostr protocol standards
/// </summary>
public class NostrRelayInfo
{
    /// <summary>
    /// String identifying the relay
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// String with detailed information about the relay
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    /// <summary>
    /// A link to a banner image
    /// </summary>
    [JsonPropertyName("banner")]
    public string? Banner { get; set; }
    
    /// <summary>
    /// A link to an icon image
    /// </summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
    
    /// <summary>
    /// Administrative contact public key
    /// </summary>
    [JsonPropertyName("pubkey")]
    public string? Pubkey { get; set; }
    
    /// <summary>
    /// Administrative alternate contact
    /// </summary>
    [JsonPropertyName("contact")]
    public string? Contact { get; set; }
    
    /// <summary>
    /// List of NIP numbers supported by the relay
    /// </summary>
    [JsonPropertyName("supported_nips")]
    public int[]? SupportedNips { get; set; }
    
    /// <summary>
    /// String identifying relay software URL
    /// </summary>
    [JsonPropertyName("software")]
    public string? Software { get; set; }
    
    /// <summary>
    /// String version identifier
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    /// <summary>
    /// A link to a text file describing the relay's privacy policy
    /// </summary>
    [JsonPropertyName("privacy_policy")]
    public string? PrivacyPolicy { get; set; }

    [JsonPropertyName("posting_policy")]
    public string? PostingPolicy { get; set; }
    
    /// <summary>
    /// A link to a text file describing the relay's terms of service
    /// </summary>
    [JsonPropertyName("terms_of_service")]
    public string? TermsOfService { get; set; }
}
