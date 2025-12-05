using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Manages loading and access to the root subject.
/// Bootstraps the system from root.json configuration.
/// </summary>
public class RootManager
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<RootManager>? _logger;

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

        var fullPath = Path.GetFullPath(configPath);
        _logger?.LogInformation("Loading root configuration from: {Path}", fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {fullPath}", fullPath);
        }

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
        Root = _serializer.Deserialize(json, _context);

        if (Root == null)
        {
            throw new InvalidOperationException("Failed to deserialize root configuration");
        }

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);

        // Allow caller to handle post-load customization (e.g., path resolution)
        var configDir = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        postLoad?.Invoke(Root, configDir);

        // Register root in context for easy access
        _context.AddService(Root);
    }
}
