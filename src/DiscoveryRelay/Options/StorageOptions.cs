namespace DiscoveryRelay.Options;

/// <summary>
/// Options for configuring the storage provider
/// </summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// The type of storage provider to use
    /// </summary>
    public string Provider { get; set; } = "Lmdb";

    /// <summary>
    /// The GUID used for authenticating stop/start database API calls
    /// </summary>
    public string ApiAuthenticationGuid { get; set; } = "";
}
