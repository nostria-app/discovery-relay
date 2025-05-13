namespace DiscoveryRelay.Options;

/// <summary>
/// Options for configuring Azure Blob Storage
/// </summary>
public class AzureBlobOptions
{
    public const string ConfigSection = "AzureBlob";

    /// <summary>
    /// The connection string for Azure Blob Storage.
    /// If using Managed Identity, this can be left empty.
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

    /// <summary>
    /// Whether to use Azure Managed Identity for authentication instead of a connection string
    /// </summary>
    public bool UseManagedIdentity { get; set; } = false;

    /// <summary>
    /// The Azure Storage account name when using Managed Identity
    /// Required when UseManagedIdentity is true
    /// </summary>
    public string AccountName { get; set; } = "";

    /// <summary>
    /// The Azure Storage account endpoint suffix (e.g., core.windows.net)
    /// Used when UseManagedIdentity is true
    /// </summary>
    public string EndpointSuffix { get; set; } = "core.windows.net";
}
