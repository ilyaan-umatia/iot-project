using Application.Api.Configuration;
using Application.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Services.Configure<BrokerSettings>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<PrimaryHostSettings>(builder.Configuration.GetSection("Sparkplug"));
var databasePath = builder.Configuration["Storage:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "state", "backend.db");
builder.Services.AddSingleton(_ => new TelemetryStore(databasePath));
builder.Services.AddSingleton<GpioMessageService>();
builder.Services.AddHostedService<MqttIngestService>();
builder.Services.AddControllers();

var app = builder.Build();
app.MapGet("/", () => "IoT application is running.");
app.MapGet("/health", () => "IoT application is running.");
app.MapControllers();
app.Run();
