using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscoveryRelay.Models;

public class NostrMessageJsonConverter : JsonConverter<NostrMessage>
{
    public override NostrMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array");
        }

        // Read the array opening bracket
        reader.Read();

        // Read the message type
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected message type string");
        }

        var messageType = reader.GetString();
        
        switch (messageType?.ToUpper())
        {
            case "REQ":
                return ReadReqMessage(ref reader, options);
            case "CLOSE":
                return ReadCloseMessage(ref reader, options);
            default:
                // Skip over the rest of the unknown message
                SkipToEndOfArray(ref reader);
                return null;
        }
    }

    private NostrReqMessage ReadReqMessage(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var message = new NostrReqMessage();
        
        // Read subscription ID
        reader.Read();
        if (reader.TokenType == JsonTokenType.String)
        {
            message.SubscriptionId = reader.GetString() ?? string.Empty;
        }
        else
        {
            throw new JsonException("Expected subscription ID string");
        }

        // Read filter object
        reader.Read();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            message.Filter = JsonSerializer.Deserialize<Dictionary<string, object>>(ref reader, options) ?? 
                             new Dictionary<string, object>();
        }
        
        // Skip to end of array
        SkipToEndOfArray(ref reader);
        
        return message;
    }

    private NostrCloseMessage ReadCloseMessage(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var message = new NostrCloseMessage();
        
        // Read subscription ID
        reader.Read();
        if (reader.TokenType == JsonTokenType.String)
        {
            message.SubscriptionId = reader.GetString() ?? string.Empty;
        }
        else
        {
            throw new JsonException("Expected subscription ID string");
        }
        
        // Skip to end of array
        SkipToEndOfArray(ref reader);
        
        return message;
    }

    private void SkipToEndOfArray(ref Utf8JsonReader reader)
    {
        // Skip remaining elements until end of array
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }
    }

    public override void Write(Utf8JsonWriter writer, NostrMessage value, JsonSerializerOptions options)
    {
        throw new NotImplementedException("Writing Nostr messages is not implemented");
    }
}
