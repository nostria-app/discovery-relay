using System.Text.Json.Serialization;
using DiscoveryRelay;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using DiscoveryRelay.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Cors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<RelayOptions>(
    builder.Configuration.GetSection(RelayOptions.SectionName));

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

// Map controllers for REST API - do this before custom middleware
app.MapControllers();

// Use a dedicated endpoint for Nostr relay info to ensure proper routing and CORS handling
app.MapGet("/", (HttpContext context, IOptions<RelayOptions> options) =>
{
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
            Version = relayOptions.Version,
            PrivacyPolicy = relayOptions.PrivacyPolicy,
            TermsOfService = relayOptions.TermsOfService
        };

        context.Response.ContentType = "application/nostr+json";
        return Results.Json(relayInfo, NostrSerializationContext.Default.NostrRelayInfo);
    }
    
    // For other requests to the root, let the pipeline continue
    return Results.Empty;
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

// Enable static files - should come after other middleware
app.UseDefaultFiles();
app.UseStaticFiles();

// Make sure index.html and other static files are properly served
app.MapFallbackToFile("index.html");

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

app.Run();

public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
