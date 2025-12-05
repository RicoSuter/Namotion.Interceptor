using HomeBlaze.Core.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage;

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
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LoadAsync(string configPath = "root.json", CancellationToken cancellationToken = default)
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

        // Resolve relative paths in FileSystemStorage relative to root.json location
        var configDir = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        if (Root is FileSystemStorage storage && !Path.IsPathRooted(storage.Path))
        {
            storage.Path = Path.GetFullPath(Path.Combine(configDir, storage.Path));
            _logger?.LogInformation("Resolved storage path to: {Path}", storage.Path);
        }

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);

        // Register root in context for easy access
        _context.AddService(Root);
    }

    /// <summary>
    /// Gets a subject by navigating a path from the root.
    /// Path segments are separated by '/'.
    /// </summary>
    public IInterceptorSubject? GetByPath(string path)
    {
        if (Root == null || string.IsNullOrWhiteSpace(path))
            return Root;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IInterceptorSubject? current = Root;

        foreach (var segment in segments)
        {
            if (current == null)
                return null;

            // Try to get child from storage container
            if (current is StorageContainer container)
            {
                if (container.Children.TryGetValue(segment, out var child))
                {
                    current = child;
                    continue;
                }
            }

            // Could add support for other navigation patterns here
            return null;
        }

        return current;
    }
}
