namespace DiscoveryRelay.Options;

public class LmdbOptions
{
    public const string ConfigSection = "Lmdb";
    
    /// <summary>
    /// The path to the LMDB database directory
    /// </summary>
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");
}
