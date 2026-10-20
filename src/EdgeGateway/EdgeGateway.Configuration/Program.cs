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
if (store.IsEmpty)
    foreach (var device in SeedDevices()) store.Save(device, create: true);

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

// Three demo fridges on the pymodbus simulators. Registers: holding 0 = temperature x10 (int16),
// holding 1 = humidity x10 (uint16), holding 10-11 = compressor power in W (float32),
// holding 12-13 = door openings (uint32 counter), coil 0 = door switch (GPIO input).
static IEnumerable<DeviceDefinition> SeedDevices() =>
    new (string Id, int Port, int Pin)[] { ("fridge-01", 5021, 17), ("fridge-02", 5022, 18), ("fridge-03", 5023, 27) }
        .Select(fridge => new DeviceDefinition(fridge.Id, true, DeviceDefinition.ModbusTcp, "127.0.0.1", fridge.Port, 1,
            3, fridge.Pin, $"Demo fridge on Modbus TCP port {fridge.Port}",
            [
                new("Temperature", "holding", 0, 0.1, "°C", "int16"),
                new("Humidity", "holding", 1, 0.1, "%", "uint16"),
                new("CompressorPower", "holding", 10, 1, "W", "float32", "big"),
                new("DoorOpenings", "holding", 12, 1, "count", "uint32", "big"),
                new("GpioValue", "coil", 0, 1, "door", "bool")
            ]));
