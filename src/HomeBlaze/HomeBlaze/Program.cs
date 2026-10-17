using HomeBlaze.AI;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.ClaudeCli;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Components;
using HomeBlaze.History.Blazor;
using HomeBlaze.History.InMemory.Blazor;
using HomeBlaze.History.Mcp;
using HomeBlaze.History.Sqlite.Blazor;
using HomeBlaze.Host;
using HomeBlaze.Samples;
using HomeBlaze.OpcUa;
using HomeBlaze.OpcUa.Blazor;
using HomeBlaze.Plugins;
using HomeBlaze.Services;
using HomeBlaze.Services.Diagnostics;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor;
using HomeBlaze.Storage.Blazor.Files;
using Namotion.Devices.Gpio;
using Namotion.Devices.Gpio.HomeBlaze;
using Namotion.Devices.MyStrom;
using Namotion.Devices.MyStrom.HomeBlaze;
using Namotion.Devices.Shelly;
using Namotion.Devices.Shelly.HomeBlaze;
using Namotion.Devices.Sonos;
using Namotion.Devices.Sonos.HomeBlaze;
using Namotion.Devices.FritzBox;
using Namotion.Devices.FritzBox.HomeBlaze;
using Namotion.Devices.SunSpec;
using Namotion.Devices.SunSpec.HomeBlaze;
using Namotion.Devices.Wallbox;
using Namotion.Devices.Wallbox.HomeBlaze;
using Namotion.Devices.Ecowitt;
using Namotion.Devices.Ecowitt.HomeBlaze;
using Namotion.Devices.Luxtronik;
using Namotion.Devices.Luxtronik.HomeBlaze;
using Namotion.Devices.Philips.Hue;
using Namotion.Devices.Philips.Hue.HomeBlaze;
using Toolbelt.Blazor.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Add all HomeBlaze services (cascades: Host -> Host.Services -> Services)
// This registers the singleton IInterceptorSubjectContext with HostedServiceHandler
builder.Services.AddHomeBlazeHost();
builder.Services.AddHomeBlazeStorage();

// Seeding must run before AddHomeBlazePlugins, which reads the plugin configuration during registration.
var rootConfigurationPath = HomeBlazePaths.GetRootConfigurationPath(builder.Configuration);
var seedDirectory = builder.Configuration[HomeBlazePaths.SeedDirectoryKey];
var seededFileCount = DataDirectorySeeder.SeedIfMissing(rootConfigurationPath, seedDirectory);

var pluginConfigPath = HomeBlazePaths.GetPluginConfigurationPath(builder.Configuration);

builder.Services.AddHomeBlazePlugins(pluginConfigPath);
builder.Services.AddHotKeys2();
builder.Services.AddPropertyHistoryViewer();

// The history package contributes its own tool; the AI package knows nothing about it. Registered once so
// both the external MCP server and the built-in agents get it.
builder.Services.AddHomeBlazeMcpToolProvider(context => new HistoryMcpToolProvider(
    context.RootSubjectProvider,
    context.PathProvider,
    context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<HistoryMcpToolProvider>()));
builder.Services.AddHomeBlazeAgents();

// The MCP server is always registered because the built-in agents use it; McpServer:Enabled only decides
// whether the external /mcp route is mapped (default: false, enabled in Development).
var mcpEnabled = builder.Configuration.GetValue("McpServer:Enabled", false);
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithHomeBlazeMcpTools(isReadOnly: builder.Configuration.GetValue("McpServer:ReadOnly", true));

// Add services to the container.
builder.Services
    .AddHttpClient();

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (seededFileCount > 0)
{
    app.Logger.LogInformation("Seeded the data directory with {Count} default files.", seededFileCount);
}

// Replaced on every start, unlike the seeded files, so the documentation always matches the running version.
// It must stay before app.Run, which starts the storage file watcher. Outdated documentation must not keep
// the application down, so a failure is only logged.
try
{
    var replacedFileCount = DataDirectorySeeder.ReplaceDocumentation(rootConfigurationPath, seedDirectory);
    if (replacedFileCount > 0)
    {
        app.Logger.LogInformation("Replaced the documentation with {Count} files from the seed directory.", replacedFileCount);
    }
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    app.Logger.LogWarning(exception, "Could not replace the documentation from the seed directory. It may be outdated or incomplete until the next start.");
}

// Configure TypeProvider with application-specific assemblies
// This must happen before any service that depends on TypeProvider is used
var typeProvider = app.Services.GetRequiredService<TypeProvider>();
typeProvider
    .AddAssembly(typeof(HostSystem).Assembly)                  // HomeBlaze.Services
    .AddAssembly(typeof(FluentStorageContainer).Assembly)      // HomeBlaze.Storage
    .AddAssembly(typeof(MarkdownFilePageComponent).Assembly)   // HomeBlaze.Storage.Blazor
    .AddAssembly(typeof(Widget).Assembly)                      // HomeBlaze.Components
    .AddAssembly(typeof(Motor).Assembly)                       // HomeBlaze.Samples
    .AddAssembly(typeof(OpcUaServer).Assembly)                 // HomeBlaze.OpcUa
    .AddAssembly(typeof(OpcUaServerEditComponent).Assembly)    // HomeBlaze.OpcUa.Blazor
    .AddAssembly(typeof(GpioSubject).Assembly)
    .AddAssembly(typeof(GpioSubjectEditComponent).Assembly)
    .AddAssembly(typeof(HueBridge).Assembly)
    .AddAssembly(typeof(HueBridgeSetupComponent).Assembly)
    .AddAssembly(typeof(MyStromSwitch).Assembly)
    .AddAssembly(typeof(MyStromSwitchWidget).Assembly)
    .AddAssembly(typeof(ShellyDevice).Assembly)
    .AddAssembly(typeof(ShellyDeviceWidget).Assembly)
    .AddAssembly(typeof(SunSpecDevice).Assembly)
    .AddAssembly(typeof(SunSpecDeviceWidget).Assembly)
    .AddAssembly(typeof(WallboxCharger).Assembly)
    .AddAssembly(typeof(WallboxChargerWidget).Assembly)
    .AddAssembly(typeof(EcowittGateway).Assembly)
    .AddAssembly(typeof(EcowittGatewayWidget).Assembly)
    .AddAssembly(typeof(HomeBlaze.History.InMemory.InMemoryHistoryStoreSubject).Assembly)   // HomeBlaze.History.InMemory
    .AddAssembly(typeof(InMemoryHistoryStoreEditComponent).Assembly)                        // HomeBlaze.History.InMemory.Blazor
    .AddAssembly(typeof(HomeBlaze.History.Sqlite.SqliteHistoryStoreSubject).Assembly)       // HomeBlaze.History.Sqlite
    .AddAssembly(typeof(SqliteHistoryStoreEditComponent).Assembly)                          // HomeBlaze.History.Sqlite.Blazor
    .AddAssembly(typeof(HistoryChart).Assembly)                                             // HomeBlaze.History.Blazor
    .AddAssembly(typeof(HomeBlaze.Installation.HomeBlazeInstallation).Assembly)             // HomeBlaze.Installation
    .AddAssembly(typeof(HomeBlaze.Installation.Blazor.InstallationWidget).Assembly)         // HomeBlaze.Installation.Blazor
    .AddAssembly(typeof(LuxtronikHeatPump).Assembly)                                        // Namotion.Devices.Luxtronik
    .AddAssembly(typeof(LuxtronikHeatPumpWidget).Assembly)                                  // Namotion.Devices.Luxtronik.HomeBlaze
    .AddAssembly(typeof(SonosSystem).Assembly)                                              // Namotion.Devices.Sonos
    .AddAssembly(typeof(SonosSystemWidget).Assembly)                                        // Namotion.Devices.Sonos.HomeBlaze
    .AddAssembly(typeof(FritzBox).Assembly)                                                 // Namotion.Devices.FritzBox
    .AddAssembly(typeof(FritzBoxWidget).Assembly)                                           // Namotion.Devices.FritzBox.HomeBlaze
    .AddAssembly(typeof(ClaudeCliAgent).Assembly)                                           // HomeBlaze.AI
    .AddAssembly(typeof(HomeBlaze.AI.Blazor.AgentPage).Assembly);                           // HomeBlaze.AI.Blazor

// Register HomeBlaze.Plugins subject types
typeProvider.AddAssembly(typeof(PluginManager).Assembly);

// Load runtime plugins
var pluginLoader = app.Services.GetRequiredService<PluginLoader>();
var pluginResult = await pluginLoader.LoadPluginsAsync(CancellationToken.None);
if (pluginResult != null)
{
    foreach (var plugin in pluginResult.LoadedPlugins)
    {
        foreach (var assembly in plugin.Assemblies)
        {
            typeProvider.AddAssembly(assembly);
        }
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    // The CLI agent calls back over HTTP on the same host; a redirect would drop its bearer token.
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments(AgentMcpEndpoint.Route),
        branch => branch.UseHttpsRedirection());
}

app.UseAntiforgery();

app.MapHomeBlazeAgentMcp();

// Map the external MCP server endpoint if enabled
if (mcpEnabled)
{
    app.MapMcp("/mcp");
}

app.MapDefaultEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
