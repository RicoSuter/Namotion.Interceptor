using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Base class for storage containers that hold child subjects.
/// Implements BackgroundService for self-contained lifecycle management.
/// </summary>
[InterceptorSubject]
public abstract partial class StorageContainer : BackgroundService, IIconProvider, ITitleProvider
{
    // MudBlazor Icons.Material.Filled.Storage
    private const string StorageIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M2,20H22V16H2M2,4V8H22V4M2,14H22V10H2Z\" /></svg>";

    public virtual string? Icon => StorageIcon;
    public virtual string? Title => Name;

    private readonly ILogger? _logger;
    private int _retryCount;
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Display name of this container.
    /// </summary>
    [Configuration]
    public partial string? Name { get; set; }

    /// <summary>
    /// Child subjects indexed by name/key.
    /// </summary>
    [State(Order = 10)]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    /// <summary>
    /// Current status of the storage container.
    /// </summary>
    [State(Order = 1)]
    public partial StorageStatus Status { get; set; }

    /// <summary>
    /// Error message if status is Error.
    /// </summary>
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Last successful scan time.
    /// </summary>
    [State("Last Scan", Order = 2)]
    public partial DateTime? LastScanTime { get; set; }

    protected StorageContainer()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
        Status = StorageStatus.Initializing;
    }

    protected StorageContainer(ILogger? logger) : this()
    {
        _logger = logger;
    }

    protected StorageContainer(IInterceptorSubjectContext context, ILogger? logger) : this(context)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Status = StorageStatus.Scanning;
                ErrorMessage = null;

                await ScanAsync(stoppingToken);

                LastScanTime = DateTime.UtcNow;
                Status = StorageStatus.Connected;
                _retryCount = 0;

                await WatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in storage container {Type}", GetType().Name);
                Status = StorageStatus.Error;
                ErrorMessage = ex.Message;

                _retryCount++;
                if (_retryCount <= MaxRetries)
                {
                    var delay = InitialRetryDelay * Math.Pow(2, _retryCount - 1);
                    _logger?.LogInformation("Retrying in {Delay} (attempt {Attempt}/{Max})",
                        delay, _retryCount, MaxRetries);
                    await Task.Delay(delay, stoppingToken);
                }
                else
                {
                    _logger?.LogWarning("Max retries reached, waiting before next attempt");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    _retryCount = 0;
                }
            }
        }

        Status = StorageStatus.Disconnected;
    }

    /// <summary>
    /// Scans the storage and populates Children.
    /// </summary>
    protected abstract Task ScanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Watches for changes in the storage. Override for real-time providers.
    /// Default implementation does nothing (polling-based containers override ScanAsync behavior).
    /// </summary>
    protected virtual Task WatchAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>
    /// Adds or updates a child subject.
    /// </summary>
    protected void AddChild(string key, IInterceptorSubject subject)
    {
        var newChildren = new Dictionary<string, IInterceptorSubject>(Children)
        {
            [key] = subject
        };
        Children = newChildren;
    }

    /// <summary>
    /// Removes a child subject.
    /// </summary>
    protected bool RemoveChild(string key)
    {
        if (!Children.ContainsKey(key))
            return false;

        var newChildren = new Dictionary<string, IInterceptorSubject>(Children);
        newChildren.Remove(key);
        Children = newChildren;
        return true;
    }
}

public enum StorageStatus
{
    Initializing,
    Scanning,
    Connected,
    Error,
    Disconnected
}
