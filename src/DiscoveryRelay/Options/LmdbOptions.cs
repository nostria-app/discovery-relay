namespace DiscoveryRelay.Options;

public class LmdbOptions
{
    public const string ConfigSection = "Lmdb";

    /// <summary>
    /// The path to the LMDB database directory
    /// </summary>
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Size of the database in megabytes
    /// </summary>
    public long SizeInMb { get; set; } = 1024L; // Default to 1024MB (1GB)

    /// <summary>
    /// Maximum number of readers
    /// </summary>
    public int MaxReaders { get; set; } = 4096;

    /// <summary>
    /// Interval in seconds for logging write statistics (minimum 10 seconds)
    /// </summary>
    public int StatsIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// GUID used for authenticating stop/start database API calls
    /// </summary>
    public string ApiAuthenticationGuid { get; set; } = "";
}
