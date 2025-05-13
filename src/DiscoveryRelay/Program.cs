using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using DiscoveryRelay;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using DiscoveryRelay.Services;
using DiscoveryRelay.Utilities;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<RelayOptions>(
    builder.Configuration.GetSection(RelayOptions.SectionName));

// Configure storage options
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));

// Add storage options
builder.Services.Configure<LmdbOptions>(builder.Configuration.GetSection(LmdbOptions.ConfigSection));
builder.Services.Configure<AzureBlobOptions>(builder.Configuration.GetSection(AzureBlobOptions.ConfigSection));

// Register storage services conditionally
var storageProvider = builder.Configuration.GetSection(StorageOptions.SectionName)["Provider"]?.ToLowerInvariant();
if (storageProvider == "azureblob")
{
    builder.Services.AddSingleton<AzureBlobStorageProvider>();
    builder.Services.AddSingleton<IStorageProvider, AzureBlobStorageProvider>();
    Console.WriteLine("Configured to use Azure Blob Storage provider");
}
else
{
    // Default to LMDB
    builder.Services.AddSingleton<LmdbStorageService>();
    builder.Services.AddSingleton<LmdbStorageProvider>();
    builder.Services.AddSingleton<IStorageProvider, LmdbStorageProvider>();
    Console.WriteLine("Configured to use LMDB Storage provider");
}

// Add the EventFilterService
builder.Services.AddSingleton<EventFilterService>();

// Add CORS policy with a named policy for more control - make it as permissive as possible for debugging
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("*");
    });

    // Add a specific policy for nostr requests
    options.AddPolicy("NostrPolicy", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("*");
    });
});

// Add endpoints API explorer for OpenAPI/Swagger if used
builder.Services.AddEndpointsApiExplorer();

// Register WebSocketHandler as a singleton that will be properly disposed
builder.Services.AddSingleton<WebSocketHandler>();
builder.Services.AddHostedService<WebSocketBackgroundService>();

// Configure JSON serialization using source generation to avoid reflection
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NostrSerializationContext.Default);
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
// Do not use, there is certificate termination in front of this app.
// app.UseHttpsRedirection();

// Configure WebSocket middleware
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
};

app.UseWebSockets(webSocketOptions);

// Add explicit routing middleware - this ensures all routes are properly evaluated
app.UseRouting();

// CORS middleware must be after UseRouting but before endpoints
app.UseCors();

// Enable static files with proper defaults
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html" }
});
app.UseStaticFiles();

// Use a dedicated middleware for handling various request types at the root path
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/")
    {
        // Handle NIP-11 requests
        string acceptHeader = context.Request.Headers.Accept.ToString();
        if (acceptHeader.Contains("application/nostr+json") || context.Request.Query.ContainsKey("nostr"))
        {
            var relayOptions = context.RequestServices.GetRequiredService<IOptions<RelayOptions>>().Value;

            var relayInfo = new NostrRelayInfo
            {
                Name = relayOptions.Name,
                Description = relayOptions.Description,
                Banner = relayOptions.Banner,
                Icon = relayOptions.Icon,
                Pubkey = relayOptions.Pubkey,
                Contact = relayOptions.Contact,
                SupportedNips = relayOptions.SupportedNips,
                Software = relayOptions.Software,
                Version = VersionInfo.GetCurrentVersion(),
                PrivacyPolicy = relayOptions.PrivacyPolicy,
                PostingPolicy = relayOptions.PostingPolicy,
                TermsOfService = relayOptions.TermsOfService
            };

            context.Response.ContentType = "application/nostr+json";
            await context.Response.WriteAsJsonAsync(relayInfo, NostrSerializationContext.Default.NostrRelayInfo);
            return;
        }
        // Handle WebSocket requests
        else if (context.WebSockets.IsWebSocketRequest)
        {
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var handler = context.RequestServices.GetRequiredService<WebSocketHandler>();
            await handler.HandleWebSocketAsync(context, webSocket);
            return;
        }
    }

    // Continue with the pipeline for all other requests
    await next();
});

// Migrated API from HomeController
var apiGroup = app.MapGroup("/api");

apiGroup.MapGet("/version", () =>
    Results.Ok(new VersionResponse(VersionInfo.GetCurrentVersion())));

apiGroup.MapGet("/status", () =>
    Results.Ok(new StatusResponse("online", DateTime.UtcNow)));

apiGroup.MapGet("/stats", (
    WebSocketHandler webSocketHandler,
    IStorageProvider storageProvider,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Retrieving relay statistics");

    var connections = new ConnectionInfo(
        webSocketHandler.GetActiveConnectionCount(),
        webSocketHandler.GetTotalSubscriptionCount()
    );

    var response = new StatsResponse(
        DateTime.UtcNow,
        connections,
        storageProvider.GetStorageStatsAsync().Result
    );

    return Results.Ok(response);
});

// Stop database endpoint
apiGroup.MapGet("/stop", async (
    string key,
    IOptions<StorageOptions> options,
    IStorageProvider storageProvider,
    ILogger<Program> logger) =>
{
    if (string.IsNullOrEmpty(options.Value.ApiAuthenticationGuid))
    {
        logger.LogWarning("Storage control API authentication is not configured. Access denied.");
        return Results.Unauthorized();
    }

    if (string.IsNullOrEmpty(key) || key != options.Value.ApiAuthenticationGuid)
    {
        logger.LogWarning("Invalid authentication GUID provided for storage stop operation");
        return Results.Unauthorized();
    }

    logger.LogInformation("Authorized request to stop storage service received");
    bool result = await storageProvider.StopAsync();

    if (result)
    {
        return Results.Ok(new { message = "Storage service stopped successfully" });
    }
    else
    {
        return Results.BadRequest(new { error = "Failed to stop storage service, may already be stopped" });
    }
});

// Start database endpoint
apiGroup.MapGet("/start", async (
    string key,
    IOptions<StorageOptions> options,
    IStorageProvider storageProvider,
    ILogger<Program> logger) =>
{
    if (string.IsNullOrEmpty(options.Value.ApiAuthenticationGuid))
    {
        logger.LogWarning("Storage control API authentication is not configured. Access denied.");
        return Results.Unauthorized();
    }

    if (string.IsNullOrEmpty(key) || key != options.Value.ApiAuthenticationGuid)
    {
        logger.LogWarning("Invalid authentication GUID provided for storage start operation");
        return Results.Unauthorized();
    }

    logger.LogInformation("Authorized request to start storage service received");
    bool result = await storageProvider.StartAsync();

    if (result)
    {
        return Results.Ok(new { message = "Storage service started successfully" });
    }
    else
    {
        return Results.BadRequest(new { error = "Failed to start storage service, may already be running" });
    }
});

apiGroup.MapPost("/broadcast", async (
    BroadcastRequest request,
    WebSocketHandler webSocketHandler,
    ILogger<Program> logger) =>
{
    if (string.IsNullOrEmpty(request.Message))
    {
        return Results.BadRequest(new ErrorResponse("Message is required"));
    }

    logger.LogInformation("Broadcasting message: {Message}", request.Message);
    await webSocketHandler.BroadcastMessageAsync(request.Message);

    return Results.Ok(new DiscoveryRelay.Models.BroadcastResponse(true));
});

// Migrated API from DidController
var didGroup = app.MapGroup("/.well-known/did/nostr");

didGroup.MapGet("{pubkey}.json", async (
    string pubkey,
    IStorageProvider storageProvider,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Retrieving DID document for public key: {PubKey}", pubkey);

    // Normalize pubkey (ensure it's lowercase)
    pubkey = pubkey.ToLowerInvariant();

    // Validate the pubkey format (should be 64 hex characters)
    if (!ValidateHexPubkey(pubkey))
    {
        logger.LogWarning("Invalid pubkey format: {PubKey}", pubkey);
        return Results.BadRequest(new ErrorResponse("Invalid pubkey format. Expected 64 hex characters."));
    }

    // Try to get the event first from kind 10002, then from kind 3
    NostrEvent? nostrEvent = await storageProvider.GetEventByPubkeyAndKindAsync(pubkey, 10002);
    if (nostrEvent == null)
    {
        nostrEvent = await storageProvider.GetEventByPubkeyAndKindAsync(pubkey, 3);
        if (nostrEvent == null)
        {
            logger.LogWarning("No events found for pubkey: {PubKey}", pubkey);

            // If no events found, return a basic DID document without relay information
            var basicDocument = DidNostrDocument.FromPubkey(pubkey);
            return Results.Ok(basicDocument);
        }
    }

    // Parse relays from the event and construct the DID document
    var didDocument = BuildDidDocument(pubkey, nostrEvent);

    logger.LogInformation("Successfully created DID document for {PubKey}", pubkey);
    return Results.Ok(didDocument);
});

var sampleTodos = new Todo[] {
    new(1, "Walk the dog"),
    new(2, "Do the dishes", DateOnly.FromDateTime(DateTime.Now)),
    new(3, "Do the laundry", DateOnly.FromDateTime(DateTime.Now.AddDays(1))),
    new(4, "Clean the bathroom"),
    new(5, "Clean the car", DateOnly.FromDateTime(DateTime.Now.AddDays(2)))
};

var todosApi = app.MapGroup("/todos");
todosApi.MapGet("/", () => sampleTodos);
todosApi.MapGet("/{id}", (int id) =>
    sampleTodos.FirstOrDefault(a => a.Id == id) is { } todo
        ? Results.Ok(todo)
        : Results.NotFound());

// Make sure the fallback comes AFTER all API routes are registered
app.MapFallbackToFile("index.html");

// Helper methods migrated from DidController
bool ValidateHexPubkey(string pubkey)
{
    // Pubkey should be 64 hex characters
    if (pubkey.Length != 64)
    {
        return false;
    }

    // All characters should be valid hex
    return Regex.IsMatch(pubkey, "^[0-9a-fA-F]{64}$");
}

DidNostrDocument BuildDidDocument(string pubkey, NostrEvent nostrEvent)
{
    // Create the base DID document
    var didDocument = DidNostrDocument.FromPubkey(pubkey);

    // Parse relays from the event
    var serviceList = new List<Service>();

    // For kind 3 (contacts), extract relays from the content
    if (nostrEvent.Kind == 10002)
    {
        // In kind 10002, relays are in the tags in the format ["r", <relay-url>]
        int relayIndex = 1;
        foreach (var tag in nostrEvent.Tags)
        {
            if (tag.Count >= 2 && tag[0] == "r" && !string.IsNullOrEmpty(tag[1]))
            {
                serviceList.Add(new Service
                {
                    Id = $"{didDocument.Id}#relay{relayIndex}",
                    Type = "Relay",
                    ServiceEndpoint = tag[1]
                });
                relayIndex++;
            }
        }
    }
    // For kind 10002 (relay list), parse the relays from the "r" tags
    else if (nostrEvent.Kind == 3)
    {
        try
        {
            if (nostrEvent.Content != "")
            {
                // In kind 3, relays are in the content as JSON
                var relayDict = JsonSerializer.Deserialize<Dictionary<string, object>>(nostrEvent.Content);
                if (relayDict != null)
                {
                    int relayIndex = 1;
                    foreach (var relay in relayDict.Keys)
                    {
                        serviceList.Add(new Service
                        {
                            Id = $"{didDocument.Id}#relay{relayIndex}",
                            Type = "Relay",
                            ServiceEndpoint = relay
                        });
                        relayIndex++;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            var logger = app.Services.GetService<ILogger<Program>>();
            logger?.LogWarning(ex, "Failed to parse relay list from kind 3 event content");
        }
    }

    // Look for website or storage information in tags
    if (nostrEvent.Tags != null)
    {
        foreach (var tag in nostrEvent.Tags)
        {
            if (tag.Count >= 2 && tag[0] == "website" && !string.IsNullOrEmpty(tag[1]))
            {
                // Add website as a service
                serviceList.Add(new Service
                {
                    Id = $"{didDocument.Id}#website",
                    Type = new[] { "Website", "LinkedDomains" },
                    ServiceEndpoint = tag[1]
                });
            }
            else if (tag.Count >= 2 && tag[0] == "storage" && !string.IsNullOrEmpty(tag[1]))
            {
                // Add storage endpoint as a service
                serviceList.Add(new Service
                {
                    Id = $"{didDocument.Id}#storage",
                    Type = "Storage",
                    ServiceEndpoint = tag[1]
                });
            }
        }
    }

    // Update the document with services
    didDocument.Service = serviceList.ToArray();

    return didDocument;
}

app.Run();

// Keep this class for BroadcastRequest
public class BroadcastRequest
{
    public string? Message { get; set; }
}

public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}

public record VersionResponse(string Version);

public record StatusResponse(string Status, DateTime Timestamp);

public record ConnectionInfo(int ActiveConnections, int TotalSubscriptions);

public record StatsResponse(DateTime Timestamp, ConnectionInfo Connections, object DatabaseStats);

public record ErrorResponse(string Error);
