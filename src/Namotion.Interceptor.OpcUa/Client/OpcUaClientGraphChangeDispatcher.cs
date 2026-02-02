using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Actor-style dispatcher for OPC UA remote change events.
/// Uses a single-consumer channel to ensure thread-safe, ordered processing.
/// </summary>
internal sealed class OpcUaClientGraphChangeDispatcher : IAsyncDisposable
{
    private readonly Channel<object> _channel;
    private readonly ILogger _logger;
    private readonly Func<object, CancellationToken, Task> _processChange;
    private Task? _consumerTask;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isStopped;

    /// <summary>
    /// Marker type for periodic resync requests.
    /// </summary>
    public sealed record PeriodicResyncRequest;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaClientGraphChangeDispatcher"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic messages.</param>
    /// <param name="processChange">The callback to invoke for each queued change.</param>
    public OpcUaClientGraphChangeDispatcher(
        ILogger logger,
        Func<object, CancellationToken, Task> processChange)
    {
        _logger = logger;
        _processChange = processChange;
        _channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Starts the consumer task that processes queued changes.
    /// </summary>
    public void Start()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeAsync(_cancellationTokenSource.Token));
    }

    /// <summary>
    /// Enqueues a model change event for processing.
    /// </summary>
    /// <param name="change">The change event to enqueue.</param>
    public void EnqueueModelChange(object change)
    {
        if (!_channel.Writer.TryWrite(change))
        {
            _logger.LogWarning("Failed to enqueue model change - channel closed.");
        }
    }

    /// <summary>
    /// Enqueues a periodic resync request.
    /// </summary>
    public void EnqueuePeriodicResync()
    {
        EnqueueModelChange(new PeriodicResyncRequest());
    }

    /// <summary>
    /// Stops the dispatcher gracefully, draining remaining items.
    /// </summary>
    public async Task StopAsync()
    {
        if (_isStopped)
        {
            return;
        }

        _isStopped = true;
        _channel.Writer.TryComplete();

        if (_consumerTask is not null)
        {
            await _consumerTask.ConfigureAwait(false);
        }

        await (_cancellationTokenSource?.CancelAsync() ?? Task.CompletedTask);
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _consumerTask = null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _processChange(change, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error processing change: {ChangeType}", change.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
    }
}
