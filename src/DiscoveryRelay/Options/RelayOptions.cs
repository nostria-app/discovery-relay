namespace DiscoveryRelay.Options;

public class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// Time in minutes after which inactive WebSocket connections will be disconnected
    /// </summary>
    public int DisconnectTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Frequency in minutes to log connection statistics
    /// </summary>
    public int StatsLogIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Name of the relay
    /// </summary>
    public string Name { get; set; } = "Discovery Relay";

    /// <summary>
    /// Description of the relay
    /// </summary>
    public string Description { get; set; } = "A lightweight Nostr relay for discovery purposes";

    /// <summary>
    /// Banner image URL for the relay
    /// </summary>
    public string? Banner { get; set; }

    /// <summary>
    /// Icon image URL for the relay
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Public key of the relay
    /// </summary>
    public string? Pubkey { get; set; }

    /// <summary>
    /// Contact information for the relay
    /// </summary>
    public string? Contact { get; set; }

    /// <summary>
    /// Supported NIPs by the relay
    /// </summary>
    public int[]? SupportedNips { get; set; }

    /// <summary>
    /// Software URL for the relay
    /// </summary>
    public string Software { get; set; } = "https://github.com/sondreb/discovery-relay";

    /// <summary>
    /// Privacy policy URL for the relay
    /// </summary>
    public string? PrivacyPolicy { get; set; }

    /// <summary>
    /// Posting policy URL for the relay
    /// </summary>
    public string? PostingPolicy { get; set; }

    /// <summary>
    /// Terms of service URL for the relay
    /// </summary>
    public string? TermsOfService { get; set; }

    /// <summary>
    /// Limitations for the relay
    /// </summary>
    public RelayLimitations Limitations { get; set; } = new RelayLimitations();
}

public class RelayLimitations
{
    /// <summary>
    /// Maximum allowed length for WebSocket messages in bytes
    /// </summary>
    public int MaxMessageLength { get; set; } = 64 * 1024; // Default to 64KB if not specified
}
