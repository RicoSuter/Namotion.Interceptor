using MudBlazor.Services;
using HomeBlaze.Components;
using HomeBlaze.Core.Services;
using HomeBlaze.Core.Subjects;
using HomeBlaze.Storage;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using GenericFile = HomeBlaze.Storage.Files.GenericFile;
using JsonFile = HomeBlaze.Storage.Files.JsonFile;
using MarkdownFile = HomeBlaze.Storage.Files.MarkdownFile;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Create InterceptorSubjectContext with all interceptors
var context = SubjectContextFactory.Create(builder.Services);

// Set up type registry with assembly scanning
var typeRegistry = new SubjectTypeRegistry()
    .ScanAssemblies(typeof(FluentStorageContainer).Assembly, typeof(Motor).Assembly) // HomeBlaze.Storage and Core types
    .Register<FluentStorageContainer>()
    .Register<VirtualFolder>()
    .Register<JsonFile>()
    .Register<MarkdownFile>()
    .Register<GenericFile>()
    .Register<Motor>();

// Set up component registry with assembly scanning
var componentRegistry = new SubjectComponentRegistry()
    .ScanAssemblies(typeof(App).Assembly); // HomeBlaze UI components

// Register services
builder.Services.AddSingleton<IInterceptorSubjectContext>(context);
builder.Services.AddSingleton(typeRegistry);
builder.Services.AddSingleton(componentRegistry);

// Register serializer with factory pattern
builder.Services.AddSingleton(sp => new SubjectSerializer(typeRegistry, sp));

// Register RootManager with factory pattern
builder.Services.AddSingleton(sp => new RootManager(
    typeRegistry,
    sp.GetRequiredService<SubjectSerializer>(),
    context,
    sp.GetService<ILogger<RootManager>>()));

// Register StorageService (singleton so we can access it to register handlers)
builder.Services.AddSingleton<StorageService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageService>());

// Add hosted service to load root on startup
builder.Services.AddHostedService<RootLoaderService>();

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

/// <summary>
/// Hosted service that loads the root configuration on startup.
/// </summary>
public class RootLoaderService : IHostedService
{
    private readonly RootManager _rootManager;
    private readonly StorageService _storageService;
    private readonly ILogger<RootLoaderService> _logger;

    public RootLoaderService(RootManager rootManager, StorageService storageService, ILogger<RootLoaderService> logger)
    {
        _rootManager = rootManager;
        _storageService = storageService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _rootManager.LoadAsync("root.json", ResolveStoragePaths, cancellationToken);
            _logger.LogInformation("Root loaded successfully");

            // Connect to storage and load children
            if (_rootManager.Root is FluentStorageContainer storage)
            {
                await storage.ConnectAsync(cancellationToken);
                _logger.LogInformation("Storage connected, loaded {Count} children", storage.Children.Count);
            }

            // Register root subject with RootManager for auto-saving
            _storageService.RegisterSubject(_rootManager.Root!, _rootManager);
            _logger.LogInformation("Root subject registered with StorageService");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load root configuration");
            throw;
        }
    }

    /// <summary>
    /// Resolves relative paths in storage containers to be relative to the config file location.
    /// </summary>
    private void ResolveStoragePaths(IInterceptorSubject root, string configDir)
    {
        if (root is FluentStorageContainer storage && !Path.IsPathRooted(storage.ConnectionString))
        {
            storage.ConnectionString = Path.GetFullPath(Path.Combine(configDir, storage.ConnectionString));
            _logger.LogInformation("Resolved storage path to: {Path}", storage.ConnectionString);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
