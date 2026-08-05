using NoctisCoverProxy;

var builder = WebApplication.CreateBuilder(args);

// PublicBaseUrl: the externally-reachable URL of this server.
// Set via appsettings.json, environment variable, or command line.
// Example: --PublicBaseUrl "https://myproxy.example.com"
var publicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5123";

var app = builder.Build();

var store = new CoverArtStore();
var handler = new WebSocketHandler(store, publicBaseUrl);

app.UseWebSockets();

// WebSocket endpoint for Noctis clients
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket connections only");
        return;
    }

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await handler.HandleAsync(ws, ctx.RequestAborted);
});

// HTTP endpoint for Discord to fetch cover art images
app.MapGet("/art/{clientId}/{contentId}", (string clientId, string contentId, HttpContext ctx) =>
{
    var entry = store.Get($"{clientId}/{contentId}");
    if (entry == null)
        return Results.NotFound();

    return Results.File(entry.Value.Bytes, entry.Value.ContentType);
});

// Health check
app.MapGet("/", () => "NoctisCoverProxy is running");

Console.WriteLine($"Cover art proxy starting. PublicBaseUrl={publicBaseUrl}");
app.Run();
