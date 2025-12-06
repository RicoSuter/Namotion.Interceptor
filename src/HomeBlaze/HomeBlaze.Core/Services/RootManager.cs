using HomeBlaze.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Manages loading and access to the root subject.
/// Bootstraps the system from root.json configuration.
/// </summary>
public class RootManager : ISubjectStorageHandler, IDisposable
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<RootManager>? _logger;
    private string? _configPath;

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
        SubjectSerializer serializer,
        IInterceptorSubjectContext context,
        ILogger<RootManager>? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Loads the root subject from the configuration file.
    /// </summary>
    /// <param name="configPath">Path to the root.json configuration file.</param>
    /// <param name="postLoad">Optional callback to run after loading (e.g., to resolve relative paths).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LoadAsync(
        string configPath = "root.json",
        Action<IInterceptorSubject, string>? postLoad = null,
        CancellationToken cancellationToken = default)
    {
        if (Root != null)
        {
            _logger?.LogWarning("Root already loaded, skipping");
            return;
        }

        _configPath = Path.GetFullPath(configPath);
        _logger?.LogInformation("Loading root configuration from: {Path}", _configPath);

        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {_configPath}", _configPath);
        }

        var json = await File.ReadAllTextAsync(_configPath, cancellationToken);
        Root = _serializer.Deserialize(json, _context);

        if (Root == null)
        {
            throw new InvalidOperationException("Failed to deserialize root configuration");
        }

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);

        // Allow caller to handle post-load customization (e.g., path resolution)
        var configDir = Path.GetDirectoryName(_configPath) ?? Environment.CurrentDirectory;
        postLoad?.Invoke(Root, configDir);

        // Register root in context for easy access
        _context.AddService(Root);
    }

    /// <summary>
    /// Writes the root subject configuration to disk if this is the root subject.
    /// Called by StorageService when [Configuration] properties change.
    /// </summary>
    public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (subject != Root)
            return false;

        if (string.IsNullOrEmpty(_configPath))
            throw new InvalidOperationException("Cannot save: config path is not set");

        _logger?.LogInformation("Saving root configuration to: {Path}", _configPath);

        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(_configPath, json, ct);

        _logger?.LogInformation("Root configuration saved successfully");
        return true;
    }

    /// <summary>
    /// Saves the root subject configuration to the configuration file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Root == null)
        {
            throw new InvalidOperationException("Cannot save: root is not loaded");
        }

        await WriteAsync(Root, cancellationToken);
    }

    /// <summary>
    /// Disposes resources (no-op, StorageService handles persistence).
    /// </summary>
    public void Dispose()
    {
        // No subscriptions to dispose - StorageService handles persistence
    }
}
