using System.Text.Json.Serialization;

namespace DiscoveryRelay.Models;

/// <summary>
/// Represents a DID Document for the did:nostr method
/// </summary>
public class DidNostrDocument
{
    [JsonPropertyName("@context")]
    public string[] Context { get; set; } = { "https://www.w3.org/ns/did/v1", "https://w3id.org/nostr/context" };

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("verificationMethod")]
    public VerificationMethod[] VerificationMethod { get; set; } = Array.Empty<VerificationMethod>();

    [JsonPropertyName("authentication")]
    public string[] Authentication { get; set; } = Array.Empty<string>();

    [JsonPropertyName("assertionMethod")]
    public string[] AssertionMethod { get; set; } = Array.Empty<string>();

    [JsonPropertyName("service")]
    public Service[] Service { get; set; } = Array.Empty<Service>();

    // Helper method to create a basic DID document from a public key
    public static DidNostrDocument FromPubkey(string pubkey)
    {
        var didId = $"did:nostr:{pubkey}";
        var keyId = $"{didId}#key1";

        return new DidNostrDocument
        {
            Id = didId,
            VerificationMethod = new[]
            {
                new VerificationMethod
                {
                    Id = keyId,
                    Controller = didId,
                    Type = "SchnorrVerification2025"
                }
            },
            Authentication = new[] { "#key1" },
            AssertionMethod = new[] { "#key1" }
        };
    }
}

/// <summary>
/// Represents a verification method in a DID Document
/// </summary>
public class VerificationMethod
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("controller")]
    public string Controller { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Represents a service endpoint in a DID Document
/// </summary>
public class Service
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public object Type { get; set; } = string.Empty;

    [JsonPropertyName("serviceEndpoint")]
    public string ServiceEndpoint { get; set; } = string.Empty;
}