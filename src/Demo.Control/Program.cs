using Demo.Control;

// Demo console: controls and observes every component from one page. Listens on loopback only,
// because it can start and kill processes on this machine.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddCommandLine(args);
// The shared "Urls" setting is the backend's address; read it before this app sets its own.
builder.Configuration["Backend:Url"] ??= (builder.Configuration["Urls"] ?? "http://localhost:5114").Split(';')[0];
builder.WebHost.UseUrls(builder.Configuration["Control:Url"] ?? "http://127.0.0.1:5200");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<ComponentManager>();
builder.Services.AddSingleton<ResilienceScenario>();
builder.Services.AddSingleton<WireMonitor>();
builder.Services.AddHostedService(services => services.GetRequiredService<WireMonitor>());
builder.Services.AddHttpClient("backend", client => client.Timeout = TimeSpan.FromSeconds(2));

var app = builder.Build();
var components = app.Services.GetRequiredService<ComponentManager>();
app.Lifetime.ApplicationStopping.Register(components.Dispose);
_ = Task.Run(async () =>
{
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try { await components.RefreshContainersAsync(); } catch (Exception) { }
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", async (ResilienceScenario scenario, WireMonitor monitor) => new
{
    components = components.Describe(),
    gateway = components.ReadGatewayStatus(),
    backendReachable = await components.BackendReachableAsync(),
    fridges = components.DescribeFridges(),
    monitors = new { upstream = monitor.Connected("upstream"), edge = monitor.Connected("edge") },
    scenario = scenario.Snapshot()
});
app.MapGet("/api/wire/{broker}", (string broker, WireMonitor monitor) =>
    broker is "upstream" or "edge" ? Results.Ok(monitor.Recent(broker)) : Results.NotFound());
app.MapGet("/api/logs/{name}", (string name) => components.Tail(name, 150));

// Read-only pass-through to the backend API, so the page keeps working while the backend is down.
app.MapGet("/api/backend/{**path}", async (string path, HttpContext context, IHttpClientFactory factory) =>
{
    if (path.Contains("..")) return Results.BadRequest();
    try
    {
        var response = await factory.CreateClient("backend")
            .GetAsync($"{components.BackendUrl}/{path}{context.Request.QueryString}");
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/components/{name}/{action}", (string name, string action) =>
    Run(() => components.ActAsync(name, action)));
app.MapPost("/api/fridges/{id}/{command}", (string id, string command) =>
    Run(() => Task.FromResult(components.SendFridgeCommand(id, command))));
app.MapPost("/api/demo/start-all", () => Run(async () => { await components.StartAllAsync(); return "Everything started."; }));
app.MapPost("/api/demo/stop-all", () => Run(async () => { await components.StopAllAsync(); return "Everything stopped."; }));
app.MapPost("/api/demo/reset", () => Run(async () => { await components.ResetAsync(); return "Fresh brokers and state; everything started."; }));
app.MapPost("/api/demo/resilience", (ResilienceScenario scenario) =>
    scenario.TryStart() ? Results.Accepted() : Results.Conflict(new { message = "Scenario already running." }));

Console.WriteLine($"Demo console: {builder.Configuration["Control:Url"] ?? "http://127.0.0.1:5200"}");
app.Run();

static async Task<IResult> Run(Func<Task<string>> action)
{
    try { return Results.Ok(new { message = await action() }); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeoutException)
    {
        return Results.BadRequest(new { message = error.Message });
    }
}
