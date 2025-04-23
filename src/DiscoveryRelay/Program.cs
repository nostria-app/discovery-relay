using System.Text.Json.Serialization;
using DiscoveryRelay;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using DiscoveryRelay.Controllers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<RelayOptions>(
    builder.Configuration.GetSection(RelayOptions.SectionName));

// Add CORS policy with a named policy for more control
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
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

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

// Enable CORS with the named policy - it's important this comes early in the pipeline
app.UseCors("AllowAll");

// Configure WebSocket middleware
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
};

app.UseWebSockets(webSocketOptions);

// Map controllers for REST API - do this before custom middleware
app.MapControllers();

// Map a specific GET endpoint for Nostr relay information
app.MapGet("/", async (HttpContext context, IOptions<RelayOptions> options) =>
{
    // Check if request is asking for Nostr relay information
    string? acceptHeader = context.Request.Headers.Accept.ToString();
    if (acceptHeader != null && acceptHeader.Contains("application/nostr+json"))
    {
        var relayOptions = options.Value;
        
        // Create the relay information document
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
        
        // Add CORS headers explicitly for this endpoint for extra safety
        context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, OPTIONS");
        context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Accept");
        
        await context.Response.WriteAsJsonAsync(relayInfo, NostrSerializationContext.Default.NostrRelayInfo);
        return;
    }
    
    // If not a specific Nostr request, return a 404 or redirect to another page
    context.Response.StatusCode = 404;
    await context.Response.WriteAsync("Not Found");
}).RequireCors("AllowAll"); // Explicitly require CORS on this endpoint

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
//app.MapFallbackToFile("index.html");

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
