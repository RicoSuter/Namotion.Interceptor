using System.Collections.Immutable;
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

    // Serializes the writes of the root configuration file. Two at once would both open the file, and the one that
    // serialized the earlier state could write last.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // The issues of the root configuration file, which a write would lose. Set under the write lock, before Root, and
    // read without it by ThrowIfWriteRefused, whose result a write checks again under the lock.
    private ImmutableArray<string> _configurationIssues = [];

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
        var skipped = new List<SkippedConfigurationProperty>();

        // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
        var root = _serializer.Deserialize(json, created: null, skipped.Add) as IInterceptorSubject ??
            throw new InvalidOperationException("Failed to deserialize root configuration");
        var issues = _serializer.ReportIssues(root, json, skipped);
        if (!issues.IsEmpty)
        {
            _logger?.LogWarning(
                "The root configuration has issues and is not saved until they are corrected in the file and HomeBlaze is restarted: {Issues}",
                string.Join("; ", issues));
        }

        // Free: nothing is written before Root is set.
        await _writeLock.WaitAsync(CancellationToken.None);
        _configurationIssues = issues;
        _writeLock.Release();

        Root = root;
        Root.Context.AddFallbackContext(_context);

        _logger?.LogInformation("Root loaded: {Type}", Root.GetType().FullName);
        _context.AddService(Root);

        // Publish readiness before deferral disposal releases deferred starts.
        _rootLoaded.TrySetResult(Root);

        return Root;
    }

    /// <summary>
    /// Writes the root configuration to disk when the subject is the root or nested in its configuration.
    /// Writes run one after the other.
    /// </summary>
    /// <exception cref="ConfigurationWriteRefusedException">
    /// The root configuration file has issues, such as a value that could not be read, which writing it would lose. The
    /// file is read only at the start, so saving works again once they are corrected in the file and HomeBlaze is
    /// restarted.
    /// </exception>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var root = Root;
        if (root == null || subject.TryGetConfigurationOwner(candidate => ReferenceEquals(candidate, root)) == null)
            return false;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_configurationIssues.IsEmpty)
                throw CreateWriteRefusal(_configurationIssues);

            _logger?.LogInformation("Saving root configuration to: {Path}", ConfigurationPath);

            var json = _serializer.Serialize(root);
            await File.WriteAllTextAsync(ConfigurationPath, json, cancellationToken);

            _logger?.LogInformation("Root configuration saved successfully");
        }
        finally
        {
            _writeLock.Release();
        }

        return true;
    }

    /// <summary>
    /// Throws the refusal that <see cref="WriteConfigurationAsync"/> would throw now when the subject is the root or
    /// nested in its configuration.
    /// </summary>
    /// <exception cref="ConfigurationWriteRefusedException">
    /// The root configuration file has issues, such as a value that could not be read, which writing it would lose.
    /// </exception>
    public void ThrowIfWriteRefused(IInterceptorSubject subject)
    {
        var root = Root;
        var issues = _configurationIssues;
        if (root != null && !issues.IsEmpty && subject.TryGetConfigurationOwner(candidate => ReferenceEquals(candidate, root)) != null)
            throw CreateWriteRefusal(issues);
    }

    private ConfigurationWriteRefusedException CreateWriteRefusal(ImmutableArray<string> issues) => new(
        $"The configuration is not saved, because the root configuration file '{ConfigurationPath}' has issues that saving would lose: " +
        $"{string.Join("; ", issues)}. Correct them in the file and restart HomeBlaze, which reads it only at the start; then saving works again.",
        issues);
}
