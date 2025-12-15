using HomeBlaze.Components;
using HomeBlaze.Host;
using HomeBlaze.Samples;
using HomeBlaze.Servers.OpcUa;
using HomeBlaze.Servers.OpcUa.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor.Files;
using Toolbelt.Blazor.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add all HomeBlaze services (cascades: Host -> Host.Services -> Services)
// This registers the singleton IInterceptorSubjectContext with HostedServiceHandler
builder.Services.AddHomeBlazeHost();
builder.Services.AddHotKeys2();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure TypeProvider with application-specific assemblies
// This must happen before any service that depends on TypeProvider is used
var typeProvider = app.Services.GetRequiredService<TypeProvider>();
typeProvider.AddAssemblies(
    typeof(FluentStorageContainer).Assembly,      // HomeBlaze.Storage
    typeof(MarkdownFilePageComponent).Assembly,   // HomeBlaze.Storage.Blazor
    typeof(Widget).Assembly,                      // HomeBlaze.Components
    typeof(Motor).Assembly,                       // HomeBlaze.Samples
    typeof(OpcUaServer).Assembly,                 // HomeBlaze.Servers.OpcUa
    typeof(OpcUaServerEditComponent).Assembly,    // HomeBlaze.Servers.OpcUa.Blazor
    typeof(App).Assembly);                        // HomeBlaze UI components

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
