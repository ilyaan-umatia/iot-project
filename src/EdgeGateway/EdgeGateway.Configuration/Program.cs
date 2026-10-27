using EdgeGateway.Configuration;
using IoT.Contracts;

// Configuration layer: the device registry. It says which devices exist and how to read them;
// whether a device is online is known only to data acquisition, which reports it as Sparkplug births/deaths.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.WebHost.UseUrls(builder.Configuration["Configuration:Url"] ?? "http://127.0.0.1:5300");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy =
    EdgeTopics.Json.PropertyNamingPolicy);
var databasePath = builder.Configuration["Configuration:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "state", "configuration.db");
builder.Services.AddSingleton(_ => new DeviceStore(databasePath));

var app = builder.Build();
var store = app.Services.GetRequiredService<DeviceStore>();

app.MapGet("/", () => "Edge gateway configuration service.");
app.MapGet("/health", () => "ok");
app.MapGet("/config", () => store.Snapshot());
app.MapGet("/devices", () => store.All());
app.MapGet("/devices/{id}", (string id) => store.Find(id) is { } device ? Results.Ok(device) : Results.NotFound());
app.MapPost("/devices", (DeviceDefinition device) => Write(() => store.Save(device, create: true)
    ? Results.Created($"/devices/{device.Id}", device)
    : Results.Conflict(new { message = $"Device {device.Id} already exists." })));
app.MapPut("/devices/{id}", (string id, DeviceDefinition device) => Write(() => device.Id != id
    ? Results.BadRequest(new { message = "The device ID in the body must match the URL." })
    : store.Save(device, create: false) ? Results.Ok(device) : Results.NotFound()));
app.MapDelete("/devices/{id}", (string id) => store.Delete(id) ? Results.NoContent() : Results.NotFound());

Console.WriteLine($"Configuration service: {builder.Configuration["Configuration:Url"] ?? "http://127.0.0.1:5300"} ({store.All().Length} devices).");
app.Run();

static IResult Write(Func<IResult> action)
{
    try { return action(); }
    catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
}

