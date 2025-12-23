using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Manages loading and access to the root subject.
/// Bootstraps the system from root.json configuration.
/// </summary>
public class RootManager : BackgroundService, IConfigurationWriter
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly IInterceptorSubjectContext _context;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<RootManager>? _logger;
    private string? _configurationPath;

    /// <summary>
    /// The root subject loaded from configuration.
    /// </summary>
    public IInterceptorSubject? Root { get; internal set; }

    /// <summary>
    /// Whether the root has been loaded.
    /// </summary>
    public bool IsLoaded => Root != null;

    public RootManager(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IInterceptorSubjectContext context,
        IConfiguration? configuration = null,
        ILogger<RootManager>? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _context = context;
        _configuration = configuration;
        _logger = logger;

        // Register self with context for subjects to access
        context.AddService(this);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (Root != null)
        {
            return;
        }

        var configFileName = _configuration?["HomeBlaze:RootConfigFile"] ?? "root.json";
        _configurationPath = Path.GetFullPath(configFileName);
        _logger?.LogInformation("Loading root configuration from: {Path}", _configurationPath);

        if (!File.Exists(_configurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {_configurationPath}", _configurationPath);
        }

        var json = await File.ReadAllTextAsync(_configurationPath, cancellationToken);
        var root = _serializer.Deserialize(json);

        // All IConfigurableSubject implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
        Root = root as IInterceptorSubject ?? throw new InvalidOperationException("Failed to deserialize root configuration");
        Root.Context.AddFallbackContext(_context);

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);
        _context.AddService(Root);
    }

    /// <summary>
    /// Writes the root subject configuration to disk if this is the root subject.
    /// Called by ConfigurationManager when [Configuration] properties change.
    /// </summary>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (subject != Root)
            return false;

        if (string.IsNullOrEmpty(_configurationPath))
            throw new InvalidOperationException("Cannot save: config path is not set");

        _logger?.LogInformation("Saving root configuration to: {Path}", _configurationPath);

        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(_configurationPath, json, cancellationToken);

        _logger?.LogInformation("Root configuration saved successfully");
        return true;
    }
}
