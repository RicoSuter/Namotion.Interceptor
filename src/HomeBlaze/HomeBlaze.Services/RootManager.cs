using HomeBlaze.Abstractions.Storage;
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
    private readonly ILogger<RootManager>? _logger;
    private string? _configurationPath;

    /// <summary>
    /// The root subject loaded from configuration.
    /// </summary>
    public IInterceptorSubject? Root { get; private set; }

    /// <summary>
    /// Whether the root has been loaded.
    /// </summary>
    public bool IsLoaded => Root != null;

    public RootManager(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IInterceptorSubjectContext context,
        ILogger<RootManager>? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _context = context;
        _logger = logger;
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

        _configurationPath = Path.GetFullPath("root.json");
        _logger?.LogInformation("Loading root configuration from: {Path}", _configurationPath);

        if (!File.Exists(_configurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {_configurationPath}", _configurationPath);
        }

        var json = await File.ReadAllTextAsync(_configurationPath, cancellationToken);

        Root = _serializer.Deserialize(json);
        if (Root == null)
        {
            throw new InvalidOperationException("Failed to deserialize root configuration");
        }

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

    /// <summary>
    /// Saves the root subject configuration to the configuration file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Root == null)
        {
            throw new InvalidOperationException("Cannot save: root is not loaded");
        }

        await WriteConfigurationAsync(Root, cancellationToken);
    }
}
