using System.Reactive.Disposables;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Connects a subject graph to a Beckhoff TwinCAT PLC via ADS protocol.
/// Thin orchestrator composing <see cref="AdsConnectionManager"/> and <see cref="AdsSubscriptionManager"/>.
/// </summary>
internal sealed class TwinCatSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly AdsSubjectLoader _subjectLoader;
    private readonly AdsConnectionManager _connectionManager;
    private readonly AdsSubscriptionManager _subscriptionManager;
    private readonly SemaphoreSlim _rescanSignal = new(0, 1);
    private readonly Lock _rescanLock = new();

    private volatile SubjectPropertyWriter? _propertyWriter;
    private long _lastRescanRequestedAtTicks; // DateTimeOffset.UtcNow.UtcTicks, 0 = no pending request

    private AdsClientDiagnostics? _diagnostics;
    private int _disposed; // 0 = false, 1 = true

    /// <summary>
    /// Initializes a new instance of the <see cref="TwinCatSubjectClientSource"/> class.
    /// </summary>
    /// <param name="subject">The root subject to synchronize with the PLC.</param>
    /// <param name="configuration">The ADS client configuration.</param>
    /// <param name="logger">The logger instance.</param>
    public TwinCatSubjectClientSource(
        IInterceptorSubject subject,
        AdsClientConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        _subject = subject;
        _configuration = configuration;
        _logger = logger;

        _connectionManager = new AdsConnectionManager(configuration, logger);
        _subscriptionManager = new AdsSubscriptionManager(configuration, logger);
        _subjectLoader = new AdsSubjectLoader(configuration.PathProvider);

        _ownership = new SourceOwnershipManager(
            this,
            onReleasing: _subscriptionManager.OnPropertyReleasing,
            onSubjectDetaching: _subscriptionManager.OnSubjectDetaching);

        // Wire connection events to request debounced rescan via ExecuteAsync loop
        _connectionManager.ConnectionRestored += () => RequestRescan("ADS connection restored.");
        _connectionManager.ConnectionLost += () => _propertyWriter?.StartBuffering();
        _connectionManager.AdsStateEnteredRun += () => RequestRescan("PLC entered Run state.");
        _connectionManager.SymbolVersionChanged += () => RequestRescan("Symbol version changed.");
    }

    /// <summary>
    /// Gets the ADS client configuration (internal for testing).
    /// </summary>
    internal AdsClientConfiguration Configuration => _configuration;

    /// <summary>
    /// Gets the connection manager (internal for testing and diagnostics).
    /// </summary>
    internal AdsConnectionManager ConnectionManager => _connectionManager;

    /// <summary>
    /// Gets the subscription manager (internal for diagnostics).
    /// </summary>
    internal AdsSubscriptionManager SubscriptionManager => _subscriptionManager;

    /// <inheritdoc />
    public IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public int WriteBatchSize => 0; // No limit - sequential writes

    /// <inheritdoc />
    public async Task<IDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        await _connectionManager.ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
        FullRescan();

        // Return a wrapper that calls ClearAll() instead of the raw CompositeDisposable.
        // CompositeDisposable.Dispose() is permanent (future Add() calls immediately dispose),
        // while ClearAll() uses Clear() which keeps the CompositeDisposable reusable.
        return Disposable.Create(() => _subscriptionManager.ClearAll());
    }

    /// <inheritdoc />
    public async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            return null;
        }

        var properties = new List<(RegisteredSubjectProperty Property, ISymbol Symbol)>();
        var symbols = new List<ISymbol>();

        foreach (var propertyReference in _ownership.Properties)
        {
            var registeredProperty = propertyReference.TryGetRegisteredProperty();
            if (registeredProperty is null)
            {
                continue;
            }

            var symbolPath = _subscriptionManager.GetSymbolPath(propertyReference);
            if (symbolPath is null)
            {
                continue;
            }

            var symbol = AdsSubscriptionManager.TryGetSymbol(_connectionManager.SymbolLoader, symbolPath);
            if (symbol is not null)
            {
                properties.Add((registeredProperty, symbol));
                symbols.Add(symbol);
            }
        }

        if (properties.Count == 0)
        {
            return null;
        }

        // Try batch read via SumSymbolRead first, fall back to individual reads
        var values = new (RegisteredSubjectProperty Property, object? Value)[properties.Count];

        try
        {
            var sumRead = new SumSymbolRead(connection, symbols);
            var readResult = await sumRead.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (readResult is { ErrorCode: AdsErrorCode.NoError, Values: not null })
            {
                // Cache array references to avoid repeated property access in the loop
                var resultValues = readResult.Values;
                var subErrors = readResult.SubErrors;
                for (var index = 0; index < properties.Count && index < resultValues.Length; index++)
                {
                    if (subErrors is not null && index < subErrors.Length && subErrors[index] != AdsErrorCode.NoError)
                    {
                        continue;
                    }

                    values[index] = (properties[index].Property,
                        _configuration.ValueConverter.ConvertToPropertyValue(resultValues[index], properties[index].Property));
                }

                _logger.LogInformation("Successfully batch-read {Count} ADS symbols from PLC.", properties.Count);
            }
            else
            {
                _logger.LogDebug("SumSymbolRead not supported (Error: {ErrorCode}), falling back to individual reads.", readResult.ErrorCode);
                await ReadIndividualValuesAsync(properties, values, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "SumSymbolRead failed, falling back to individual reads.");
            await ReadIndividualValuesAsync(properties, values, cancellationToken).ConfigureAwait(false);
        }

        return () =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (property, value) in values)
            {
                if (property is not null)
                {
                    property.SetValueFromSource(this, now, now, value);
                }
            }

            _logger.LogInformation("Updated {Count} properties with PLC values.", properties.Count);
        };
    }

    private async Task ReadIndividualValuesAsync(
        List<(RegisteredSubjectProperty Property, ISymbol Symbol)> properties,
        (RegisteredSubjectProperty Property, object? Value)[] values,
        CancellationToken cancellationToken)
    {
        var successCount = 0;
        for (var index = 0; index < properties.Count; index++)
        {
            try
            {
                var readResult = await ((IValueSymbol)properties[index].Symbol)
                    .ReadValueAsync(cancellationToken).ConfigureAwait(false);
                if ((AdsErrorCode)readResult.ErrorCode == AdsErrorCode.NoError)
                {
                    values[index] = (properties[index].Property,
                        _configuration.ValueConverter.ConvertToPropertyValue(readResult.Value, properties[index].Property));
                    successCount++;
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to read symbol '{SymbolPath}'.", properties[index].Symbol.InstancePath);
            }
        }

        _logger.LogInformation("Read {SuccessCount}/{TotalCount} ADS symbols individually from PLC.", successCount, properties.Count);
    }

    /// <inheritdoc />
    public async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            return WriteResult.Failure(changes, new InvalidOperationException("ADS connection is not established."));
        }

        return await WriteChangesAsyncCore(connection, changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core write logic. Separated from <see cref="WriteChangesAsync"/> so that the
    /// connection-null guard lives in the public entry point while the processing
    /// logic can be called directly with a mock <see cref="IAdsConnection"/> in tests.
    /// </summary>
    internal async ValueTask<WriteResult> WriteChangesAsyncCore(
        IAdsConnection connection,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        try
        {
            var capacity = changes.Length;
            var symbols = new List<ISymbol>(capacity);
            var writeValues = new object[capacity];
            var writeCount = 0;
            var validChanges = new List<SubjectPropertyChange>(capacity);
            List<SubjectPropertyChange>? unresolvedChanges = null;

            foreach (var change in changes.Span)
            {
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is null)
                {
                    continue;
                }

                var symbolPath = _subscriptionManager.GetSymbolPath(change.Property);
                if (symbolPath is null)
                {
                    // Symbol path not cached — likely a rescan is in progress.
                    // Treat as transient so the retry queue picks it up.
                    (unresolvedChanges ??= []).Add(change);
                    continue;
                }

                var symbol = AdsSubscriptionManager.TryGetSymbol(_connectionManager.SymbolLoader, symbolPath);
                if (symbol is null)
                {
                    (unresolvedChanges ??= []).Add(change);
                    continue;
                }

                object? convertedValue;
                try
                {
                    convertedValue = _configuration.ValueConverter.ConvertToAdsValue(
                        change.GetNewValue<object?>(), registeredProperty);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to convert value for ADS symbol '{SymbolPath}'. Dropping write.", symbolPath);
                    continue;
                }

                if (convertedValue is null)
                {
                    _logger.LogDebug("Skipping write of null value to ADS symbol '{SymbolPath}'.", symbolPath);
                    continue;
                }

                symbols.Add(symbol);
                writeValues[writeCount++] = convertedValue;
                validChanges.Add(change);
            }

            if (symbols.Count == 0 && unresolvedChanges is null)
            {
                return WriteResult.Success;
            }

            if (symbols.Count == 0)
            {
                _logger.LogDebug("Deferring {Count} writes: symbol paths not available (rescan in progress?).", unresolvedChanges!.Count);
                return WriteResult.Failure(unresolvedChanges.ToArray(),
                    new AdsWriteException(unresolvedChanges.Count, 0, unresolvedChanges.Count));
            }

            // Trim writeValues to exact count only if some changes were skipped
            var writeArray = writeCount == capacity ? writeValues : writeValues[..writeCount];

            // Try batch write via SumSymbolWrite, fall back to individual writes
            try
            {
                var sumWrite = new SumSymbolWrite(connection, symbols);
                var sumResult = await sumWrite.WriteAsync(writeArray, cancellationToken).ConfigureAwait(false);

                if (sumResult.ErrorCode == AdsErrorCode.DeviceServiceNotSupported)
                {
                    _logger.LogDebug("SumSymbolWrite not supported, falling back to individual writes.");
                    return await WriteIndividualValuesAsync(symbols, writeArray, validChanges, unresolvedChanges, cancellationToken).ConfigureAwait(false);
                }

                if (sumResult.ErrorCode != AdsErrorCode.NoError)
                {
                    if (AdsErrorClassifier.IsTransientError(sumResult.ErrorCode))
                    {
                        // Transient batch error -- all changes (valid + unresolved) should be retried
                        var allFailed = MergeRetryChanges(validChanges, unresolvedChanges) ?? [];
                        var error = new AdsWriteException(allFailed.Length, 0, allFailed.Length);
                        return WriteResult.Failure(allFailed, error);
                    }

                    _logger.LogWarning("Permanent ADS batch write error: {ErrorCode}. Dropping {Count} writes.",
                        sumResult.ErrorCode, validChanges.Count);
                    return BuildWriteResult(null, 0, validChanges.Count, unresolvedChanges);
                }

                // Batch succeeded -- classify per-symbol errors if present
                if (sumResult.SubErrors is not null)
                {
                    return ClassifyWriteErrors(sumResult.SubErrors, validChanges, unresolvedChanges);
                }

                return BuildWriteResult(null, 0, validChanges.Count, unresolvedChanges);
            }
            catch (AdsException exception) when ((AdsErrorCode)exception.HResult == AdsErrorCode.DeviceServiceNotSupported)
            {
                _logger.LogDebug("SumSymbolWrite threw DeviceServiceNotSupported, falling back to individual writes.");
                return await WriteIndividualValuesAsync(symbols, writeArray, validChanges, unresolvedChanges, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AdsException exception)
        {
            var errorCode = (AdsErrorCode)exception.HResult;
            var isTransient = AdsErrorClassifier.IsTransientError(errorCode);
            var error = new AdsWriteException(
                isTransient ? changes.Length : 0,
                isTransient ? 0 : changes.Length,
                changes.Length);

            if (isTransient)
            {
                return WriteResult.Failure(changes, error);
            }

            _logger.LogWarning("Permanent ADS write error: {ErrorCode}. Dropping {Count} writes.",
                errorCode, changes.Length);
            return WriteResult.Success;
        }
        catch (Exception exception)
        {
            return WriteResult.Failure(changes, exception);
        }
    }

    /// <summary>
    /// Classifies per-symbol write errors from a batch result.
    /// Transient failures and unresolved changes are returned for retry.
    /// Permanent failures are logged and dropped.
    /// </summary>
    internal WriteResult ClassifyWriteErrors(
        AdsErrorCode[] errorCodes,
        List<SubjectPropertyChange> validChanges,
        List<SubjectPropertyChange>? unresolvedChanges)
    {
        List<SubjectPropertyChange>? transientFailures = null;
        var permanentCount = 0;

        for (var index = 0; index < errorCodes.Length && index < validChanges.Count; index++)
        {
            if (errorCodes[index] == AdsErrorCode.NoError)
            {
                continue;
            }

            if (AdsErrorClassifier.IsTransientError(errorCodes[index]))
            {
                (transientFailures ??= []).Add(validChanges[index]);
            }
            else
            {
                permanentCount++;
            }
        }

        return BuildWriteResult(transientFailures, permanentCount, validChanges.Count, unresolvedChanges);
    }

    private async ValueTask<WriteResult> WriteIndividualValuesAsync(
        List<ISymbol> symbols,
        object[] writeValues,
        List<SubjectPropertyChange> validChanges,
        List<SubjectPropertyChange>? unresolvedChanges,
        CancellationToken cancellationToken)
    {
        List<SubjectPropertyChange>? transientFailures = null;
        var permanentCount = 0;

        for (var index = 0; index < symbols.Count; index++)
        {
            try
            {
                await ((IValueSymbol)symbols[index]).WriteValueAsync(writeValues[index], cancellationToken).ConfigureAwait(false);
            }
            catch (AdsException exception)
            {
                if (AdsErrorClassifier.IsTransientError((AdsErrorCode)exception.HResult))
                {
                    (transientFailures ??= []).Add(validChanges[index]);
                }
                else
                {
                    permanentCount++;
                }
            }
            catch (Exception)
            {
                permanentCount++;
            }
        }

        return BuildWriteResult(transientFailures, permanentCount, validChanges.Count, unresolvedChanges);
    }

    /// <summary>
    /// Builds a WriteResult from classified write failures.
    /// Logs permanent errors, merges transient and unresolved changes for retry.
    /// </summary>
    private WriteResult BuildWriteResult(
        List<SubjectPropertyChange>? transientFailures,
        int permanentCount,
        int validChangeCount,
        List<SubjectPropertyChange>? unresolvedChanges)
    {
        if (permanentCount > 0)
        {
            _logger.LogWarning("Dropped {Count} writes due to permanent ADS errors.", permanentCount);
        }

        var retryChanges = MergeRetryChanges(transientFailures, unresolvedChanges);
        if (retryChanges is null)
        {
            return WriteResult.Success;
        }

        var transientCount = (transientFailures?.Count ?? 0) + (unresolvedChanges?.Count ?? 0);
        var error = new AdsWriteException(transientCount, permanentCount, validChangeCount);
        var successCount = validChangeCount - (transientFailures?.Count ?? 0) - permanentCount;
        return successCount > 0
            ? WriteResult.PartialFailure(retryChanges, error)
            : WriteResult.Failure(retryChanges, error);
    }

    private static SubjectPropertyChange[]? MergeRetryChanges(
        List<SubjectPropertyChange>? transientFailures,
        List<SubjectPropertyChange>? unresolvedChanges)
    {
        if (transientFailures is null && unresolvedChanges is null)
        {
            return null;
        }

        var totalCount = (transientFailures?.Count ?? 0) + (unresolvedChanges?.Count ?? 0);
        var result = new SubjectPropertyChange[totalCount];
        var offset = 0;

        if (transientFailures is not null)
        {
            transientFailures.CopyTo(result, offset);
            offset += transientFailures.Count;
        }

        if (unresolvedChanges is not null)
        {
            unresolvedChanges.CopyTo(result, offset);
        }

        return result;
    }

    /// <summary>
    /// Requests a debounced rescan. Multiple rapid calls are coalesced into a single rescan
    /// by the <see cref="ExecuteAsync"/> loop after the configured debounce time elapses.
    /// </summary>
    internal void RequestRescan(string? reason = null)
    {
        if (reason is not null)
        {
            _logger.LogInformation("{Reason} Requesting rescan.", reason);
        }

        _propertyWriter?.StartBuffering();
        Interlocked.Exchange(ref _lastRescanRequestedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        // Signal the loop; ignore if already signaled (SemaphoreSlim capped at 1)
        try { _rescanSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Performs a full rescan: clears subscriptions, reloads symbols, re-registers.
    /// Returns true if the rescan was executed, false if skipped (e.g., no connection).
    /// Synchronized via <see cref="_rescanLock"/> to prevent concurrent execution
    /// from the SBBS thread (StartListeningAsync) and the TwinCAT ExecuteAsync thread.
    /// </summary>
    private bool FullRescan()
    {
        lock (_rescanLock)
        {
            var connection = _connectionManager.Connection;
            if (connection is null)
            {
                _logger.LogDebug("Skipping rescan: ADS connection is not established.");
                return false;
            }

            _subscriptionManager.ClearAll();
            _connectionManager.RecreateSymbolLoader();

            // Load subject graph
            var graphMappings = _subjectLoader.LoadSubjectGraph(_subject);

            // Register subscriptions (determines read modes, registers notifications + polling)
            _subscriptionManager.RegisterSubscriptions(
                graphMappings,
                connection,
                _connectionManager.SymbolLoader,
                _ownership,
                _propertyWriter,
                this,
                _connectionManager);

            return true;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Use a linked CTS so that if either loop faults, the other is torn down.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var rescanTask = RunRescanLoopAsync(linkedCts.Token);
        var pollingTask = RunPollingLoopAsync(linkedCts.Token);

        var firstCompleted = await Task.WhenAny(rescanTask, pollingTask).ConfigureAwait(false);
        if (firstCompleted.IsFaulted)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(rescanTask, pollingTask).ConfigureAwait(false);
    }

    private async Task RunRescanLoopAsync(CancellationToken stoppingToken)
    {
        // This loop handles debounced rescans and periodic health monitoring.
        // Event handlers (ConnectionRestored, AdsStateEnteredRun, SymbolVersionChanged)
        // signal _rescanSignal to wake the loop immediately. A debounce period ensures
        // that rapid successive events are coalesced into a single rescan.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either a rescan signal or the health check interval
                await _rescanSignal.WaitAsync(_configuration.HealthCheckInterval, stoppingToken).ConfigureAwait(false);

                // Process pending rescan with debounce
                var requestedAtTicks = Interlocked.Read(ref _lastRescanRequestedAtTicks);
                if (requestedAtTicks > 0)
                {
                    await DebounceAndRescanAsync(requestedAtTicks, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _connectionManager.LogFirstOccurrence("Rescan", exception, "Rescan failed.");

                // Re-stamp the request so the debounce period acts as a retry backoff
                if (Interlocked.Read(ref _lastRescanRequestedAtTicks) > 0)
                {
                    Interlocked.Exchange(ref _lastRescanRequestedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                }
            }
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_configuration.PollingInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _subscriptionManager.PollValuesAsync(
                    _connectionManager, _propertyWriter, this, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _connectionManager.LogFirstOccurrence("BatchPoll", exception, "Batch polling failed.");
            }
        }
    }

    private async Task DebounceAndRescanAsync(long requestedAtTicks, CancellationToken stoppingToken)
    {
        // Wait until the debounce period has elapsed since the last request.
        // If new requests arrive during the wait, restart the debounce timer.
        while (!stoppingToken.IsCancellationRequested)
        {
            var requestedAt = new DateTimeOffset(requestedAtTicks, TimeSpan.Zero);
            var elapsed = DateTimeOffset.UtcNow - requestedAt;
            var remaining = _configuration.RescanDebounceTime - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                await _rescanSignal.WaitAsync(remaining, stoppingToken).ConfigureAwait(false);

                // Check if a newer request arrived during the wait
                var newTicks = Interlocked.Read(ref _lastRescanRequestedAtTicks);
                if (newTicks > requestedAtTicks)
                {
                    requestedAtTicks = newTicks;
                    continue; // Restart debounce with the new timestamp
                }
            }

            break;
        }

        // Execute the rescan. Only clear the request after success so that
        // a transient failure (or missing connection) causes a retry on the next loop iteration.
        _logger.LogInformation("Executing debounced rescan.");
        if (FullRescan())
        {
            await (_propertyWriter?.LoadInitialStateAndResumeAsync(stoppingToken)
                ?? Task.CompletedTask).ConfigureAwait(false);

            Interlocked.Exchange(ref _lastRescanRequestedAtTicks, 0);
        }
    }

    /// <summary>
    /// Gets diagnostic information about the client connection state.
    /// </summary>
    public AdsClientDiagnostics Diagnostics => _diagnostics ??= new AdsClientDiagnostics(this);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Cancel the stoppingToken so ExecuteAsync exits cleanly, then
        // await the loop task before disposing the resources it uses.
        Dispose();

        try
        {
            await (ExecuteTask ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when stoppingToken is cancelled during DisposeAsync.
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "ExecuteAsync faulted during disposal.");
        }

        await _subscriptionManager.DisposeAsync().ConfigureAwait(false);
        await _connectionManager.DisposeAsync().ConfigureAwait(false);
        _ownership.Dispose();
        _rescanSignal.Dispose();
    }
}
