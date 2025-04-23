using System.Text.Json.Serialization;
using DiscoveryRelay;
using DiscoveryRelay.Models;
using DiscoveryRelay.Options;
using DiscoveryRelay.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<RelayOptions>(
    builder.Configuration.GetSection(RelayOptions.SectionName));

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

// Configure WebSocket middleware
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
};

app.UseWebSockets(webSocketOptions);

// Map WebSocket endpoint to root path
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/")
    {
        // Only handle WebSocket requests, let HTTP requests pass through
        if (context.WebSockets.IsWebSocketRequest)
        {
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var handler = app.Services.GetRequiredService<WebSocketHandler>();
            await handler.HandleWebSocketAsync(context, webSocket);
            return; // Don't call next() for WebSocket connections
        }
    }
    
    // Continue with HTTP processing for non-WebSocket requests
    await next();
});

// Enable static files
app.UseDefaultFiles();
app.UseStaticFiles();

// Map controllers for REST API
app.MapControllers();

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
