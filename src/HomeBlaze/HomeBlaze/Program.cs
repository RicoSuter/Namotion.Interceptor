using HomeBlaze.AI;
using HomeBlaze.Components;
using HomeBlaze.Host;
using HomeBlaze.Samples;
using HomeBlaze.Servers.OpcUa;
using HomeBlaze.Servers.OpcUa.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor;
using HomeBlaze.Storage.Blazor.Files;
using Namotion.Devices.Gpio;
using Namotion.Devices.Gpio.HomeBlaze;
using Namotion.Devices.MyStrom;
using Namotion.Devices.MyStrom.HomeBlaze;
using Namotion.Devices.Shelly;
using Namotion.Devices.Shelly.HomeBlaze;
using Namotion.Devices.Wallbox;
using Namotion.Devices.Wallbox.HomeBlaze;
using Namotion.Devices.Ecowitt;
using Namotion.Devices.Ecowitt.HomeBlaze;
using Namotion.Devices.Philips.Hue;
using Namotion.Devices.Philips.Hue.HomeBlaze;
using Toolbelt.Blazor.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add all HomeBlaze services (cascades: Host -> Host.Services -> Services)
// This registers the singleton IInterceptorSubjectContext with HostedServiceHandler
builder.Services.AddHomeBlazeHost();
builder.Services.AddHomeBlazeStorage();
builder.Services.AddHotKeys2();

// Optionally add the MCP subject server (default: false, enabled in Development)
if (builder.Configuration.GetValue<bool>("UseMcpServer"))
{
    builder.Services.AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithHomeBlazeMcpTools(isReadOnly: true);
}

// Add services to the container.
builder.Services
    .AddHttpClient();

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure TypeProvider with application-specific assemblies
// This must happen before any service that depends on TypeProvider is used
var typeProvider = app.Services.GetRequiredService<TypeProvider>();
typeProvider
    .AddAssembly(typeof(FluentStorageContainer).Assembly)      // HomeBlaze.Storage
    .AddAssembly(typeof(MarkdownFilePageComponent).Assembly)   // HomeBlaze.Storage.Blazor
    .AddAssembly(typeof(Widget).Assembly)                      // HomeBlaze.Components
    .AddAssembly(typeof(Motor).Assembly)                       // HomeBlaze.Samples
    .AddAssembly(typeof(OpcUaServer).Assembly)                 // HomeBlaze.Servers.OpcUa
    .AddAssembly(typeof(OpcUaServerEditComponent).Assembly)    // HomeBlaze.Servers.OpcUa.Blazor
    .AddAssembly(typeof(GpioSubject).Assembly)
    .AddAssembly(typeof(GpioSubjectEditComponent).Assembly)
    .AddAssembly(typeof(HueBridge).Assembly)
    .AddAssembly(typeof(HueBridgeSetupComponent).Assembly)
    .AddAssembly(typeof(MyStromSwitch).Assembly)
    .AddAssembly(typeof(MyStromSwitchWidget).Assembly)
    .AddAssembly(typeof(ShellyDevice).Assembly)
    .AddAssembly(typeof(ShellyDeviceWidget).Assembly)
    .AddAssembly(typeof(WallboxCharger).Assembly)
    .AddAssembly(typeof(WallboxChargerWidget).Assembly)
    .AddAssembly(typeof(EcowittGateway).Assembly)
    .AddAssembly(typeof(EcowittGatewayWidget).Assembly);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

// Map MCP server endpoint if enabled
if (builder.Configuration.GetValue<bool>("UseMcpServer"))
{
    app.MapMcp("/mcp");
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
