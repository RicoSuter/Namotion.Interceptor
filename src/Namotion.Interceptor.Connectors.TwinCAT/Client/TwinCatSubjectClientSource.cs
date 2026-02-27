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

    private SubjectPropertyWriter? _propertyWriter;
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
        _connectionManager.ConnectionRestored += () =>
        {
            _logger.LogInformation("ADS connection restored. Requesting rescan.");
            RequestRescan();
        };

        _connectionManager.ConnectionLost += () =>
        {
            _propertyWriter?.StartBuffering();
        };

        _connectionManager.AdsStateEnteredRun += () =>
        {
            _logger.LogInformation("PLC entered Run state. Requesting rescan.");
            RequestRescan();
        };

        _connectionManager.SymbolVersionChanged += () =>
        {
            _logger.LogInformation("Symbol version changed. Requesting rescan.");
            RequestRescan();
        };
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

        return _subscriptionManager.Subscriptions;
    }

    /// <inheritdoc />
    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            return Task.FromResult<Action?>(null);
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
            return Task.FromResult<Action?>(null);
        }

        // Try batch read via SumSymbolRead first, fall back to individual reads
        var values = new (RegisteredSubjectProperty Property, object? Value)[properties.Count];

        try
        {
            var sumRead = new SumSymbolRead(connection, symbols);
            var readResult = sumRead.ReadAsResult();

            if (readResult is { ErrorCode: AdsErrorCode.NoError, Values: not null })
            {
                var errorCodes = readResult.SubErrors;
                for (var index = 0; index < properties.Count; index++)
                {
                    if (errorCodes is not null && errorCodes[index] != AdsErrorCode.NoError)
                    {
                        continue;
                    }

                    values[index] = (properties[index].Property,
                        _configuration.ValueConverter.ConvertToPropertyValue(readResult.Values[index], properties[index].Property));
                }

                _logger.LogInformation("Successfully batch-read {Count} ADS symbols from PLC.", properties.Count);
            }
            else
            {
                _logger.LogDebug("SumSymbolRead not supported (Error: {ErrorCode}), falling back to individual reads.", readResult.ErrorCode);
                ReadIndividualValues(properties, values);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "SumSymbolRead failed, falling back to individual reads.");
            ReadIndividualValues(properties, values);
        }

        return Task.FromResult<Action?>(() =>
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
        });
    }

    private void ReadIndividualValues(
        List<(RegisteredSubjectProperty Property, ISymbol Symbol)> properties,
        (RegisteredSubjectProperty Property, object? Value)[] values)
    {
        for (var index = 0; index < properties.Count; index++)
        {
            try
            {
                var value = ((IValueSymbol)properties[index].Symbol).ReadValue();
                values[index] = (properties[index].Property,
                    _configuration.ValueConverter.ConvertToPropertyValue(value, properties[index].Property));
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to read symbol '{SymbolPath}'.", properties[index].Symbol.InstancePath);
            }
        }

        _logger.LogInformation("Successfully read {Count} ADS symbols individually from PLC.", properties.Count);
    }

    /// <inheritdoc />
    public ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            return new ValueTask<WriteResult>(
                WriteResult.Failure(changes, new InvalidOperationException("ADS connection is not established.")));
        }

        try
        {
            var capacity = changes.Length;
            var symbols = new List<ISymbol>(capacity);
            var writeValues = new object[capacity];
            var writeCount = 0;
            var validChanges = new List<SubjectPropertyChange>(capacity);

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
                    continue;
                }

                var symbol = AdsSubscriptionManager.TryGetSymbol(_connectionManager.SymbolLoader, symbolPath);
                if (symbol is null)
                {
                    continue;
                }

                symbols.Add(symbol);
                var convertedValue = _configuration.ValueConverter.ConvertToAdsValue(
                    change.GetNewValue<object?>(), registeredProperty);
                writeValues[writeCount++] = convertedValue ?? DBNull.Value;
                validChanges.Add(change);
            }

            if (symbols.Count == 0)
            {
                return new ValueTask<WriteResult>(WriteResult.Success);
            }

            // Trim writeValues to exact count only if some changes were skipped
            var writeArray = writeCount == capacity ? writeValues : writeValues[..writeCount];

            // Try batch write via SumSymbolWrite, fall back to individual writes
            try
            {
                var sumWrite = new SumSymbolWrite(connection, symbols);
                var sumResult = sumWrite.Write(writeArray);
                var batchErrorCode = sumResult.ErrorCode;
                var errorCodes = sumResult.SubErrors;

                if (batchErrorCode == AdsErrorCode.NoError && errorCodes is not null)
                {
                    // Check individual results
                    List<SubjectPropertyChange>? failedChanges = null;
                    var transientCount = 0;
                    var permanentCount = 0;

                    for (var index = 0; index < errorCodes.Length && index < validChanges.Count; index++)
                    {
                        if (errorCodes[index] != AdsErrorCode.NoError)
                        {
                            (failedChanges ??= []).Add(validChanges[index]);
                            if (AdsErrorClassifier.IsTransientError(errorCodes[index]))
                            {
                                transientCount++;
                            }
                            else
                            {
                                permanentCount++;
                            }
                        }
                    }

                    if (failedChanges is null)
                    {
                        return new ValueTask<WriteResult>(WriteResult.Success);
                    }

                    var error = new AdsWriteException(transientCount, permanentCount, validChanges.Count);
                    var successCount = validChanges.Count - failedChanges.Count;
                    return new ValueTask<WriteResult>(
                        successCount > 0
                            ? WriteResult.PartialFailure(failedChanges.ToArray(), error)
                            : WriteResult.Failure(failedChanges.ToArray(), error));
                }

                if (batchErrorCode == AdsErrorCode.DeviceServiceNotSupported)
                {
                    _logger.LogDebug("SumSymbolWrite not supported, falling back to individual writes.");
                    return WriteIndividualValues(symbols, writeArray, validChanges);
                }

                if (batchErrorCode != AdsErrorCode.NoError)
                {
                    var isTransient = AdsErrorClassifier.IsTransientError(batchErrorCode);
                    var error = new AdsWriteException(
                        isTransient ? changes.Length : 0,
                        isTransient ? 0 : changes.Length,
                        changes.Length);
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, error));
                }
            }
            catch (AdsException exception) when ((AdsErrorCode)exception.HResult == AdsErrorCode.DeviceServiceNotSupported)
            {
                _logger.LogDebug("SumSymbolWrite threw DeviceServiceNotSupported, falling back to individual writes.");
                return WriteIndividualValues(symbols, writeArray, validChanges);
            }

            return new ValueTask<WriteResult>(WriteResult.Success);
        }
        catch (AdsException exception)
        {
            var errorCode = (AdsErrorCode)exception.HResult;
            var isTransient = AdsErrorClassifier.IsTransientError(errorCode);
            var error = new AdsWriteException(
                isTransient ? changes.Length : 0,
                isTransient ? 0 : changes.Length,
                changes.Length);
            return new ValueTask<WriteResult>(WriteResult.Failure(changes, error));
        }
        catch (Exception exception)
        {
            return new ValueTask<WriteResult>(WriteResult.Failure(changes, exception));
        }
    }

    private ValueTask<WriteResult> WriteIndividualValues(
        List<ISymbol> symbols,
        object[] writeValues,
        List<SubjectPropertyChange> validChanges)
    {
        List<SubjectPropertyChange>? failedChanges = null;
        var transientCount = 0;
        var permanentCount = 0;

        for (var index = 0; index < symbols.Count; index++)
        {
            try
            {
                ((IValueSymbol)symbols[index]).WriteValue(writeValues[index]);
            }
            catch (AdsException exception)
            {
                (failedChanges ??= []).Add(validChanges[index]);
                if (AdsErrorClassifier.IsTransientError((AdsErrorCode)exception.HResult))
                {
                    transientCount++;
                }
                else
                {
                    permanentCount++;
                }
            }
            catch (Exception)
            {
                (failedChanges ??= []).Add(validChanges[index]);
                permanentCount++;
            }
        }

        if (failedChanges is null)
        {
            return new ValueTask<WriteResult>(WriteResult.Success);
        }

        var error = new AdsWriteException(transientCount, permanentCount, validChanges.Count);
        var successCount = validChanges.Count - failedChanges.Count;
        return new ValueTask<WriteResult>(
            successCount > 0
                ? WriteResult.PartialFailure(failedChanges.ToArray(), error)
                : WriteResult.Failure(failedChanges.ToArray(), error));
    }

    /// <summary>
    /// Requests a debounced rescan. Multiple rapid calls are coalesced into a single rescan
    /// by the <see cref="ExecuteAsync"/> loop after the configured debounce time elapses.
    /// </summary>
    internal void RequestRescan()
    {
        _propertyWriter?.StartBuffering();
        Interlocked.Exchange(ref _lastRescanRequestedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        // Signal the loop; ignore if already signaled (SemaphoreSlim capped at 1)
        try { _rescanSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void FullRescan()
    {
        var connection = _connectionManager.Connection;
        if (connection is null)
        {
            _logger.LogDebug("Skipping rescan: ADS connection is not established.");
            return;
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
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
                await WaitForSignalOrTimeoutAsync(
                    _rescanSignal, _configuration.HealthCheckInterval, stoppingToken).ConfigureAwait(false);

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
                await WaitForSignalOrTimeoutAsync(
                    _rescanSignal, remaining, stoppingToken).ConfigureAwait(false);

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

        // Clear the request and execute the rescan
        Interlocked.Exchange(ref _lastRescanRequestedAtTicks, 0);

        _logger.LogInformation("Executing debounced rescan.");
        FullRescan();
        await (_propertyWriter?.LoadInitialStateAndResumeAsync(stoppingToken)
            ?? Task.CompletedTask).ConfigureAwait(false);
    }

    private static async Task WaitForSignalOrTimeoutAsync(
        SemaphoreSlim signal, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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

        await _subscriptionManager.DisposeAsync().ConfigureAwait(false);
        await _connectionManager.DisposeAsync().ConfigureAwait(false);
        _ownership.Dispose();
        _rescanSignal.Dispose();
        Dispose();
    }

}
