using MudBlazor.Services;
using HomeBlaze.Components;
using HomeBlaze.Core.Services;
using HomeBlaze.Core.Subjects;
using HomeBlaze.Storage;
using Namotion.Interceptor;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Create InterceptorSubjectContext with all interceptors
var context = SubjectContextFactory.Create(builder.Services);

// Set up type registry with assembly scanning
var typeRegistry = new SubjectTypeRegistry()
    .ScanAssemblies(typeof(FileSystemStorage).Assembly, typeof(Motor).Assembly) // HomeBlaze.Storage and Core types
    .Register<FileSystemStorage>()
    .Register<Folder>()
    .Register<MarkdownFile>()
    .Register<GenericFile>()
    .Register<Motor>();

// Set up view registry with assembly scanning
var viewRegistry = new SubjectViewRegistry()
    .ScanAssemblies(typeof(App).Assembly); // HomeBlaze UI components

// Register services
builder.Services.AddSingleton<IInterceptorSubjectContext>(context);
builder.Services.AddSingleton(typeRegistry);
builder.Services.AddSingleton(viewRegistry);

// Register serializer with factory pattern
builder.Services.AddSingleton(sp => new SubjectSerializer(typeRegistry, sp));

// Register RootManager with factory pattern
builder.Services.AddSingleton(sp => new RootManager(
    typeRegistry,
    sp.GetRequiredService<SubjectSerializer>(),
    context,
    sp.GetService<ILogger<RootManager>>()));

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
    private readonly ILogger<RootLoaderService> _logger;

    public RootLoaderService(RootManager rootManager, ILogger<RootLoaderService> logger)
    {
        _rootManager = rootManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _rootManager.LoadAsync("root.json", cancellationToken);
            _logger.LogInformation("Root loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load root configuration");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
