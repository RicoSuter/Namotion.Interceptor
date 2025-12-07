using MudBlazor.Services;
using HomeBlaze.Components;
using HomeBlaze.Core;
using HomeBlaze.Core.Components;
using HomeBlaze.Core.Pages;
using HomeBlaze.Core.Subjects;
using HomeBlaze.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Create InterceptorSubjectContext with all interceptors
var context = SubjectContextFactory.Create(builder.Services);

// Set up TypeProvider with assembly scanning
var typeProvider = new TypeProvider();
typeProvider.AddAssemblies(
    typeof(FluentStorageContainer).Assembly,  // HomeBlaze.Storage
    typeof(Motor).Assembly,                    // HomeBlaze.Core
    typeof(App).Assembly);                     // HomeBlaze UI components

// Create registries with TypeProvider
var typeRegistry = new SubjectTypeRegistry(typeProvider);
var componentRegistry = new SubjectComponentRegistry(typeProvider);

// Register services
builder.Services.AddSingleton(context);
builder.Services.AddSingleton(typeProvider);
builder.Services.AddSingleton(typeRegistry);
builder.Services.AddSingleton(componentRegistry);

// Register navigation services
var routePathResolver = new RoutePathResolver();
builder.Services.AddSingleton(routePathResolver);
builder.Services.AddSingleton(new NavigationItemResolver(componentRegistry, routePathResolver));

// Register serializer with factory pattern
builder.Services.AddSingleton(sp => new ConfigurableSubjectSerializer(typeRegistry, sp));

// Register RootManager with factory pattern
builder.Services.AddSingleton(sp => new RootManager(
    typeRegistry,
    sp.GetRequiredService<ConfigurableSubjectSerializer>(),
    context,
    sp.GetService<ILogger<RootManager>>()));

builder.Services.AddHostedService(sp => sp.GetRequiredService<RootManager>());

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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
