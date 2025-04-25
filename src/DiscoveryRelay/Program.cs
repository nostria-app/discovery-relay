using System.Text.Json.Serialization;
using DiscoveryRelay;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using DiscoveryRelay.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Cors.Infrastructure;
using DiscoveryRelay.Services;
using DiscoveryRelay.Utilities;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<RelayOptions>(
    builder.Configuration.GetSection(RelayOptions.SectionName));

// Add LMDB options and service
builder.Services.Configure<LmdbOptions>(builder.Configuration.GetSection(LmdbOptions.ConfigSection));
builder.Services.AddSingleton<LmdbStorageService>();

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

// Add services to the container.
builder.Services.AddControllers();
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

// CORS must be the very first middleware in the pipeline
app.UseCors();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

// Configure WebSocket middleware
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
};

app.UseWebSockets(webSocketOptions);

// Add explicit routing middleware - this ensures all routes are properly evaluated
app.UseRouting();

// Enable static files - should come before route handling but after basic middleware
app.UseDefaultFiles();
app.UseStaticFiles();

// Register controllers and API endpoints
app.MapControllers();

// Use a dedicated endpoint for Nostr relay info to ensure proper routing and CORS handling
app.MapGet("/", async (HttpContext context, IOptions<RelayOptions> options) =>
{
    // Check if the request is for a static file (e.g., index.html)
    if (context.Request.Headers.Accept.ToString().Contains("text/html") && !context.Request.Query.ContainsKey("nostr"))
    {
        // Serve the static file (index.html)
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync("wwwroot/index.html");
        return;
    }

    // Only handle this endpoint for Nostr relay info requests
    string acceptHeader = context.Request.Headers.Accept.ToString();
    if (acceptHeader.Contains("application/nostr+json") || context.Request.Query.ContainsKey("nostr"))
    {
        var relayOptions = options.Value;

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

    // For other requests to the root, let the pipeline continue
    context.Response.StatusCode = 404;
})
.RequireCors("NostrPolicy")
.ExcludeFromDescription(); // Don't include this in Swagger docs

// Handle WebSocket requests specifically 
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" && context.WebSockets.IsWebSocketRequest)
    {
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var handler = app.Services.GetRequiredService<WebSocketHandler>();
        await handler.HandleWebSocketAsync(context, webSocket);
    }
    else
    {
        // Continue with HTTP processing for non-WebSocket requests
        await next();
    }
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
app.MapFallbackToFile("{**path}", "index.html", new StaticFileOptions())
   .AddEndpointFilter(async (context, next) =>
   {
       // Don't apply the fallback to API routes or .well-known paths
       var path = context.HttpContext.Request.Path.Value ?? string.Empty;

       // Normalize path for comparison (handle both with and without leading slash)
       var normalizedPath = path.TrimStart('/').ToLowerInvariant();

       // Check if the path is for API or .well-known routes
       if (normalizedPath.StartsWith("api/") || normalizedPath.StartsWith(".well-known/"))
       {
           // Return NotFound to let the regular API routing handle this
           return Results.NotFound();
       }

       return await next(context);
   });

app.Run();

public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
