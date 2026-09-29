using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Polling;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Polls Modbus TCP registers into the subject properties mapped with
/// <see cref="Attributes.ModbusRegisterAttribute"/>. Read only: local changes are not sent to the device and
/// are replaced by the device value on the next poll.
/// </summary>
public sealed class ModbusSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly ModbusClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly ModbusSessionFactory _sessionFactory;
    private readonly ConcurrentDictionary<PropertyReference, byte> _writeWarnings = new(PropertyReference.Comparer);

    private ModbusSession? _session;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private volatile TaskCompletionSource? _initialLoadGate;
    private int _disposed;

    internal ModbusSubjectClientSource(IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger)
        : base(subject.Context, logger, configuration.BufferTime, configuration.RetryTime)
    {
        configuration.Validate();

        _subject = subject;
        _configuration = configuration;
        _logger = logger;
        _ownership = new SourceOwnershipManager(this);

        var pollingMetrics = new ModbusPollingMetrics();
        _sessionFactory = new ModbusSessionFactory(subject, configuration, this, _ownership, pollingMetrics, logger);

        Metrics.RegisterClaimedProperties(() => _ownership.Count);
        Metrics.RegisterResettable(pollingMetrics);
        Diagnostics = new ModbusClientDiagnostics(Metrics, pollingMetrics);
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <summary>
    /// Gets what this source reports about its connection and polling.
    /// </summary>
    public override ModbusClientDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;
        var initialLoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _initialLoadGate = initialLoadGate;

        await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        Metrics.MarkOperational();

        return BackgroundTaskLifetime.Start(
            cancellationToken,
            _logger,
            token => RunPollLoopAsync(initialLoadGate.Task, token),
            () =>
            {
                CloseSession();
                Metrics.MarkNotOperational();
                return ValueTask.CompletedTask;
            });
    }

    /// <inheritdoc />
    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session) ?? throw new InvalidOperationException("The Modbus connection is not established.");
        await session.ReadAsync(cancellationToken).ConfigureAwait(false);

        var timestamp = DateTimeOffset.UtcNow;
        var initialLoadGate = _initialLoadGate;
        return () =>
        {
            session.Poller.ApplyChanges(
                (Source: this, session.Poller, Timestamp: timestamp),
                static (state, property, value) =>
                {
                    // Isolated per property like the poll path's writes, so one rejected value cannot fail every load.
                    try
                    {
                        property.SetValueFromSource(state.Source, state.Timestamp, state.Timestamp, value);
                    }
                    catch (Exception exception)
                    {
                        state.Source._logger.LogWarning(exception, "Failed to apply the Modbus value of {PropertyPath}.", state.Poller.GetPath(property));
                    }
                });

            // Opened only after the apply, so the poll loop never reads while the initial values are applied. The writer
            // skips this action only after a later StartBuffering, and the only one this source calls waits for this gate.
            initialLoadGate?.TrySetResult();
        };
    }

    /// <inheritdoc />
    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var poller = Volatile.Read(ref _session)?.Poller;
        foreach (var change in changes.Span)
        {
            if (_writeWarnings.TryAdd(change.Property, 0))
            {
                _logger.LogWarning(
                    "Property {PropertyPath} is read from Modbus and cannot be written; the next poll restores the device value.",
                    poller?.GetPath(change.Property) ?? change.Property.Name);
            }

            poller?.RequestReapply(change.Property);
        }

        // Success, not Failure: a failure would park the change in the retry queue for good.
        return new ValueTask<WriteResult>(WriteResult.Success);
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                Volatile.Read(ref _session)?.Dispose();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        DisposeResources();
        base.Dispose();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Cancels the poll loop first, so a reconnect in flight stops before the ownership is released.
        base.Dispose();
        DisposeResources();
    }

    private async Task OpenSessionAsync(CancellationToken cancellationToken)
    {
        var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        _writeWarnings.Clear();
        Interlocked.Exchange(ref _session, session)?.Dispose();
    }

    private void CloseSession() => Interlocked.Exchange(ref _session, null)?.Dispose();

    private async Task RunPollLoopAsync(Task initialLoadCompleted, CancellationToken cancellationToken)
    {
        // A stop while waiting throws OperationCanceledException, which the lifetime expects.
        await initialLoadCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            Exception? connectionFailure = null;

            await RunAttemptAsync(cancellationToken, async attempt =>
            {
                try
                {
                    await PollUntilFailureAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || attempt.WasForceKilled)
                {
                    // A stop ends the loop below, a kill reconnects like a lost connection.
                }
                catch (Exception exception)
                {
                    connectionFailure = exception;
                }
            }).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await ReconnectAsync(connectionFailure, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls every polling interval until reading fails. Only returns by throwing.
    /// </summary>
    private async Task PollUntilFailureAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_configuration.PollingInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var session = Volatile.Read(ref _session) ?? throw new InvalidOperationException("The Modbus connection was closed.");
            var propertyWriter = _propertyWriter ?? throw new InvalidOperationException("The source is not listening.");

            await session.ReadAsync(cancellationToken).ConfigureAwait(false);

            var timestamp = DateTimeOffset.UtcNow;
            session.Poller.ApplyChanges(
                (Source: this, PropertyWriter: propertyWriter, Timestamp: timestamp),
                static (state, property, value) => state.PropertyWriter.Write(
                    (state.Source, Property: property, Value: value, state.Timestamp),
                    static update => update.Property.SetValueFromSource(update.Source, update.Timestamp, update.Timestamp, update.Value)));
        }
    }

    /// <summary>
    /// Buffers inbound updates, closes the session and opens a new one every retry time until opening it and
    /// loading the initial state succeed. <paramref name="failure"/> is <c>null</c> when a kill ended polling.
    /// </summary>
    private async Task ReconnectAsync(Exception? failure, CancellationToken cancellationToken)
    {
        if (failure is not null)
        {
            Metrics.ReportError(failure);
            _logger.LogWarning(failure, "Modbus connection to {Host}:{Port} lost. Reconnecting.", _configuration.Host, _configuration.Port);
        }
        else
        {
            _logger.LogWarning("Modbus connection to {Host}:{Port} was killed. Reconnecting.", _configuration.Host, _configuration.Port);
        }

        Metrics.MarkNotOperational();
        _propertyWriter?.StartBuffering();
        CloseSession();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_configuration.RetryTime, cancellationToken).ConfigureAwait(false);
                await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
                Metrics.MarkOperational();
                if (_propertyWriter is { } propertyWriter)
                {
                    await propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception exception)
            {
                // A stop tears down whatever the attempt was doing, so its failure is no fault.
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Metrics.ReportError(exception);
                _sessionFactory.LogReconnectFailure(exception);
                CloseSession();
                Metrics.MarkNotOperational();
            }
        }
    }

    private void DisposeResources()
    {
        CloseSession();
        _ownership.Dispose();
    }
}
