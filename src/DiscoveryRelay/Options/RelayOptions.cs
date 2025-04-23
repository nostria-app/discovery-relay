namespace DiscoveryRelay.Options;

public class RelayOptions
{
    public const string SectionName = "Relay";
    
    /// <summary>
    /// Time in minutes after which inactive WebSocket connections will be disconnected
    /// </summary>
    public int DisconnectTimeoutMinutes { get; set; } = 2;
    
    /// <summary>
    /// Frequency in minutes to log connection statistics
    /// </summary>
    public int StatsLogIntervalMinutes { get; set; } = 1;
}
