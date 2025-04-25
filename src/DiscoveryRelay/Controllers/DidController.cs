// using System.Text.Json;
// using System.Text.Json.Serialization;
// using DiscoveryRelay.Models;
// using DiscoveryRelay.Services;
// using Microsoft.AspNetCore.Mvc;

// namespace DiscoveryRelay.Controllers;

// [ApiController]
// [Route(".well-known/did/nostr")]
// public class DidController : ControllerBase
// {
//     private readonly ILogger<DidController> _logger;
//     private readonly LmdbStorageService _storageService;
//     private readonly JsonSerializerOptions _jsonOptions;

//     public DidController(
//         LmdbStorageService storageService,
//         ILogger<DidController> logger)
//     {
//         _storageService = storageService;
//         _logger = logger;
//         _jsonOptions = new JsonSerializerOptions
//         {
//             PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
//             WriteIndented = true,
//             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
//             TypeInfoResolver = NostrSerializationContext.Default
//         };
//     }

//     [HttpGet("{pubkey}.json")]
//     [ProducesResponseType(typeof(DidNostrDocument), StatusCodes.Status200OK)]
//     [ProducesResponseType(StatusCodes.Status404NotFound)]
//     public IActionResult GetDidDocument(string pubkey)
//     {
//         _logger.LogInformation("Retrieving DID document for public key: {PubKey}", pubkey);

//         // Normalize pubkey (ensure it's lowercase)
//         pubkey = pubkey.ToLowerInvariant();

//         // Validate the pubkey format (should be 64 hex characters)
//         if (!ValidateHexPubkey(pubkey))
//         {
//             _logger.LogWarning("Invalid pubkey format: {PubKey}", pubkey);
//             return BadRequest(new { error = "Invalid pubkey format. Expected 64 hex characters." });
//         }

//         // Try to get the event first from kind 10002, then from kind 3
//         NostrEvent? nostrEvent = _storageService.GetEventByPubkeyAndKind(pubkey, 10002);
//         if (nostrEvent == null)
//         {
//             nostrEvent = _storageService.GetEventByPubkeyAndKind(pubkey, 3);
//             if (nostrEvent == null)
//             {
//                 _logger.LogWarning("No events found for pubkey: {PubKey}", pubkey);

//                 // If no events found, return a basic DID document without relay information
//                 var basicDocument = DidNostrDocument.FromPubkey(pubkey);
//                 return Ok(basicDocument);
//             }
//         }

//         // Parse relays from the event and construct the DID document
//         var didDocument = BuildDidDocument(pubkey, nostrEvent);

//         _logger.LogInformation("Successfully created DID document for {PubKey}", pubkey);
//         return Ok(didDocument);
//     }

//     private bool ValidateHexPubkey(string pubkey)
//     {
//         // Pubkey should be 64 hex characters
//         if (pubkey.Length != 64)
//         {
//             return false;
//         }

//         // All characters should be valid hex
//         return System.Text.RegularExpressions.Regex.IsMatch(pubkey, "^[0-9a-fA-F]{64}$");
//     }

//     private DidNostrDocument BuildDidDocument(string pubkey, NostrEvent nostrEvent)
//     {
//         // Create the base DID document
//         var didDocument = DidNostrDocument.FromPubkey(pubkey);

//         // Parse relays from the event
//         var serviceList = new List<Service>();

//         // For kind 3 (contacts), extract relays from the content
//         if (nostrEvent.Kind == 10002)
//         {
//             // In kind 10002, relays are in the tags in the format ["r", <relay-url>]
//             int relayIndex = 1;
//             foreach (var tag in nostrEvent.Tags)
//             {
//                 if (tag.Count >= 2 && tag[0] == "r" && !string.IsNullOrEmpty(tag[1]))
//                 {
//                     serviceList.Add(new Service
//                     {
//                         Id = $"{didDocument.Id}#relay{relayIndex}",
//                         Type = "Relay",
//                         ServiceEndpoint = tag[1]
//                     });
//                     relayIndex++;
//                 }
//             }
//         }
//         // For kind 10002 (relay list), parse the relays from the "r" tags
//         else if (nostrEvent.Kind == 3)
//         {
//             try
//             {
//                 if (nostrEvent.Content != "")
//                 {
//                     // In kind 3, relays are in the content as JSON
//                     var relayDict = JsonSerializer.Deserialize<Dictionary<string, object>>(nostrEvent.Content);
//                     if (relayDict != null)
//                     {
//                         int relayIndex = 1;
//                         foreach (var relay in relayDict.Keys)
//                         {
//                             serviceList.Add(new Service
//                             {
//                                 Id = $"{didDocument.Id}#relay{relayIndex}",
//                                 Type = "Relay",
//                                 ServiceEndpoint = relay
//                             });
//                             relayIndex++;
//                         }
//                     }
//                 }
//             }
//             catch (JsonException ex)
//             {
//                 _logger.LogWarning(ex, "Failed to parse relay list from kind 3 event content");
//             }
//         }

//         // Look for website or storage information in tags
//         if (nostrEvent.Tags != null)
//         {
//             foreach (var tag in nostrEvent.Tags)
//             {
//                 if (tag.Count >= 2 && tag[0] == "website" && !string.IsNullOrEmpty(tag[1]))
//                 {
//                     // Add website as a service
//                     serviceList.Add(new Service
//                     {
//                         Id = $"{didDocument.Id}#website",
//                         Type = new[] { "Website", "LinkedDomains" },
//                         ServiceEndpoint = tag[1]
//                     });
//                 }
//                 else if (tag.Count >= 2 && tag[0] == "storage" && !string.IsNullOrEmpty(tag[1]))
//                 {
//                     // Add storage endpoint as a service
//                     serviceList.Add(new Service
//                     {
//                         Id = $"{didDocument.Id}#storage",
//                         Type = "Storage",
//                         ServiceEndpoint = tag[1]
//                     });
//                 }
//             }
//         }

//         // Update the document with services
//         didDocument.Service = serviceList.ToArray();

//         return didDocument;
//     }
// }