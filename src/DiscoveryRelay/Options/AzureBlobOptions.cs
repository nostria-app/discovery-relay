namespace DiscoveryRelay.Options;

/// <summary>
/// Options for configuring Azure Blob Storage
/// </summary>
public class AzureBlobOptions
{
    public const string ConfigSection = "AzureBlob";

    /// <summary>
    /// The connection string for Azure Blob Storage
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// The container name for storing Nostr events
    /// </summary>
    public string ContainerName { get; set; } = "nostr-events";

    /// <summary>
    /// Interval in seconds for logging write statistics (minimum 10 seconds)
    /// </summary>
    public int StatsIntervalSeconds { get; set; } = 10;
}
