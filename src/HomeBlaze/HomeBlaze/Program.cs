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
using Toolbelt.Blazor.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add all HomeBlaze services (cascades: Host -> Host.Services -> Services)
// This registers the singleton IInterceptorSubjectContext with HostedServiceHandler
builder.Services.AddHomeBlazeHost();
builder.Services.AddHomeBlazeStorage();
builder.Services.AddHotKeys2();

// Add services to the container.
builder.Services.AddRazorComponents()
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
    .AddAssembly(typeof(GpioController).Assembly)
    .AddAssembly(typeof(GpioControllerEditComponent).Assembly);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
