using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Services;

/// <summary>
/// Manages loading and access to the root subject.
/// Bootstraps the system from the root configuration file and provides the instance data directory.
/// </summary>
public class RootManager : BackgroundService, IConfigurationWriter, IDataDirectoryProvider
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<RootManager>? _logger;

    // Continuations run off the loading thread so a UI waiter cannot resume inline inside the load.
    private readonly TaskCompletionSource<IInterceptorSubject> _rootLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The root subject loaded from configuration.
    /// </summary>
    public IInterceptorSubject? Root { get; internal set; }

    /// <summary>
    /// Whether the root has been loaded. This turns true before the root's context is wired, so it
    /// is a state probe rather than a gate. Wait on <see cref="RootLoaded"/> to use the graph.
    /// </summary>
    public bool IsLoaded => Root != null;

    /// <summary>
    /// Full path of the root configuration file.
    /// </summary>
    public string ConfigurationPath { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    public RootManager(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IInterceptorSubjectContext context,
        SubjectPathResolver pathResolver,
        IConfiguration? configuration = null,
        ILogger<RootManager>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _context = context;
        _logger = logger;

        ConfigurationPath = HomeBlazePaths.GetRootConfigurationPath(configuration);
        DataDirectory = Path.GetDirectoryName(ConfigurationPath)!;

        // Register self with context for subjects to access, which also serves IDataDirectoryProvider
        // lookups: storage, history and connectors resolve their relative paths against it.
        context.AddService(this);

        // Subjects loaded below resolve their own canonical path (the history stores do it on every
        // recorded change), so the resolver has to be in the context before the graph exists. Taking
        // it as a dependency is what guarantees that ordering.
        context.AddService<ISubjectPathResolver>(pathResolver);

        // Make host logging available to context services and lifecycle handlers, which are
        // constructed by the context factory before any provider exists and so cannot inject it
        // (for example PropertyAttributeInitializer warning on a [State]/secret conflict). Runs
        // before the root graph is built in ExecuteAsync, so handlers see it during attach.
        if (loggerFactory is not null)
        {
            context.AddService(loggerFactory);
        }
    }

    /// <summary>
    /// Completes with the root subject once the background load has attached its context, and faults
    /// with the load exception so waiters do not hang on a root that will never appear.
    /// Assigning <see cref="Root"/> directly does not complete it.
    /// </summary>
    public Task<IInterceptorSubject> RootLoaded => _rootLoaded.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _rootLoaded.TrySetResult(await LoadAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancelled rather than faulted, so a waiting UI treats a normal shutdown as one.
            _rootLoaded.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (Exception exception)
        {
            _rootLoaded.TrySetException(exception);
            throw;
        }
    }

    private async Task<IInterceptorSubject> LoadAsync(CancellationToken cancellationToken)
    {
        if (Root != null)
        {
            return Root;
        }

        _logger?.LogInformation("Loading root configuration from: {Path}", ConfigurationPath);

        if (!File.Exists(ConfigurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {ConfigurationPath}", ConfigurationPath);
        }

        var json = await File.ReadAllTextAsync(ConfigurationPath, cancellationToken);
        using var startup = _context.DeferHostedServiceStarts();
        var root = _serializer.Deserialize(json);

        // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
        Root = root as IInterceptorSubject ?? throw new InvalidOperationException("Failed to deserialize root configuration");
        Root.Context.AddFallbackContext(_context);

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);
        _context.AddService(Root);

        // Publish readiness before deferral disposal releases deferred starts.
        _rootLoaded.TrySetResult(Root);

        return Root;
    }

    /// <summary>
    /// Writes the root subject configuration to disk if this is the root subject.
    /// Called by ConfigurationManager when [Configuration] properties change.
    /// </summary>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (subject != Root)
            return false;

        _logger?.LogInformation("Saving root configuration to: {Path}", ConfigurationPath);

        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(ConfigurationPath, json, cancellationToken);

        _logger?.LogInformation("Root configuration saved successfully");
        return true;
    }
}
