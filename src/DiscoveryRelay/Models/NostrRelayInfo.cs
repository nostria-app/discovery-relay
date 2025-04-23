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
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// String with detailed information about the relay
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// A link to a banner image
    /// </summary>
    public string? Banner { get; set; }
    
    /// <summary>
    /// A link to an icon image
    /// </summary>
    public string? Icon { get; set; }
    
    /// <summary>
    /// Administrative contact public key
    /// </summary>
    public string? Pubkey { get; set; }
    
    /// <summary>
    /// Administrative alternate contact
    /// </summary>
    public string? Contact { get; set; }
    
    /// <summary>
    /// List of NIP numbers supported by the relay
    /// </summary>
    public int[]? SupportedNips { get; set; }
    
    /// <summary>
    /// String identifying relay software URL
    /// </summary>
    public string? Software { get; set; }
    
    /// <summary>
    /// String version identifier
    /// </summary>
    public string? Version { get; set; }
    
    /// <summary>
    /// A link to a text file describing the relay's privacy policy
    /// </summary>
    public string? PrivacyPolicy { get; set; }
    
    /// <summary>
    /// A link to a text file describing the relay's terms of service
    /// </summary>
    public string? TermsOfService { get; set; }
}
