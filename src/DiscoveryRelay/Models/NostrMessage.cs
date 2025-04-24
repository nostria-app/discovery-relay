using System.Text.Json.Serialization;

namespace DiscoveryRelay.Models;

/// <summary>
/// Base class for Nostr protocol messages
/// </summary>
[JsonConverter(typeof(NostrMessageJsonConverter))]
public abstract class NostrMessage
{
    public string MessageType { get; set; } = string.Empty;
}

/// <summary>
/// Represents a REQ message in the Nostr protocol
/// </summary>
public class NostrReqMessage : NostrMessage
{
    public NostrReqMessage()
    {
        MessageType = "REQ";
    }

    public string SubscriptionId { get; set; } = string.Empty;
    
    public Dictionary<string, object> Filter { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Represents a CLOSE message in the Nostr protocol
/// </summary>
public class NostrCloseMessage : NostrMessage
{
    public NostrCloseMessage()
    {
        MessageType = "CLOSE";
    }

    public string SubscriptionId { get; set; } = string.Empty;
}

/// <summary>
/// Represents an EVENT message in the Nostr protocol
/// </summary>
public class NostrEventMessage : NostrMessage
{
    public NostrEventMessage()
    {
        MessageType = "EVENT";
    }

    public NostrEvent? Event { get; set; }
}
