using Cloud.Forwarder;
using Cloud.Forwarder.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Services.AddSingleton(builder.Configuration.Get<ForwarderSettings>()
    ?? throw new InvalidOperationException("Could not read appsettings.json."));
builder.Services.AddHostedService<CloudForwarder>();

await builder.Build().RunAsync();
