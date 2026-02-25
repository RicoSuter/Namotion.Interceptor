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
internal sealed class TwinCatSubjectClientSource : BackgroundService, ISubjectSource, IAdsClientDiagnosticsSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly AdsSubjectLoader _subjectLoader;
    private readonly AdsConnectionManager _connectionManager;
    private readonly AdsSubscriptionManager _subscriptionManager;

    private SubjectPropertyWriter? _propertyWriter;

#pragma warning disable CS0414 // Field assigned but not read — used for diagnostics/future health checks
    private volatile bool _isStarted;
#pragma warning restore CS0414
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

        // Wire connection events to orchestration actions
        _connectionManager.ConnectionRestored += () =>
        {
            _logger.LogInformation("ADS connection restored. Triggering full rescan.");
            _ = TriggerFullRescanAsync();
        };

        _connectionManager.ConnectionLost += () =>
        {
            _propertyWriter?.StartBuffering();
        };

        _connectionManager.AdsStateEnteredRun += () =>
        {
            _logger.LogInformation("PLC entered Run state. Triggering full rescan.");
            _ = TriggerFullRescanAsync();
        };

        _connectionManager.SymbolVersionChanged += () =>
        {
            _logger.LogInformation("Symbol version changed. Triggering full rescan.");
            _ = TriggerFullRescanAsync();
        };
    }

    /// <summary>
    /// Gets the ADS client configuration (internal for testing).
    /// </summary>
    internal AdsClientConfiguration Configuration => _configuration;

    #region ISubjectSource Implementation

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
        await FullRescanAsync(cancellationToken).ConfigureAwait(false);

        _isStarted = true;
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
            }
        }

        if (properties.Count == 0)
        {
            return Task.FromResult<Action?>(null);
        }

        // Batch read via SumSymbolRead - synchronous API, called from background thread
        var symbols = properties.Select(item => item.Symbol).ToList();
        var sumRead = new SumSymbolRead(connection, symbols);
        var readResult = sumRead.TryRead(out var adsValues, out var errorCodes);

        if (readResult != AdsErrorCode.NoError || adsValues is null)
        {
            _logger.LogWarning("Failed to read initial state from PLC. Error: {ErrorCode}", readResult);
            return Task.FromResult<Action?>(null);
        }

        var values = new (RegisteredSubjectProperty Property, object? Value)[properties.Count];
        for (var index = 0; index < properties.Count; index++)
        {
            if (errorCodes is not null && errorCodes[index] != AdsErrorCode.NoError)
            {
                continue;
            }

            values[index] = (properties[index].Property,
                _configuration.ValueConverter.ConvertToPropertyValue(adsValues[index], properties[index].Property));
        }

        _logger.LogInformation("Successfully read {Count} ADS symbols from PLC.", properties.Count);

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
            var symbols = new List<ISymbol>();
            var writeValues = new List<object>();
            var validChanges = new List<SubjectPropertyChange>();

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
                writeValues.Add(convertedValue ?? DBNull.Value);
                validChanges.Add(change);
            }

            if (symbols.Count == 0)
            {
                return new ValueTask<WriteResult>(WriteResult.Success);
            }

            var sumWrite = new SumSymbolWrite(connection, symbols);
            var writeResult = sumWrite.TryWrite(writeValues.ToArray(), out var errorCodes);

            if (writeResult == AdsErrorCode.NoError && errorCodes is not null)
            {
                // Check individual results
                var failedChanges = new List<SubjectPropertyChange>();
                var transientCount = 0;
                var permanentCount = 0;

                for (var index = 0; index < errorCodes.Length && index < validChanges.Count; index++)
                {
                    if (errorCodes[index] != AdsErrorCode.NoError)
                    {
                        failedChanges.Add(validChanges[index]);
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

                if (failedChanges.Count == 0)
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

            if (writeResult != AdsErrorCode.NoError)
            {
                var isTransient = AdsErrorClassifier.IsTransientError(writeResult);
                var error = new AdsWriteException(
                    isTransient ? changes.Length : 0,
                    isTransient ? 0 : changes.Length,
                    changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Failure(changes, error));
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

    #endregion

    #region Full Rescan

    private Task FullRescanAsync(CancellationToken cancellationToken)
    {
        _subscriptionManager.ClearAll();
        _connectionManager.RecreateSymbolLoader();

        // Load subject graph
        var graphMappings = _subjectLoader.LoadSubjectGraph(_subject);

        // Register subscriptions (determines read modes, registers notifications + polling)
        _subscriptionManager.RegisterSubscriptions(
            graphMappings,
            _connectionManager.Connection!,
            _connectionManager.SymbolLoader,
            _ownership,
            _propertyWriter,
            this,
            _connectionManager);

        return Task.CompletedTask;
    }

    private async Task TriggerFullRescanAsync()
    {
        try
        {
            _propertyWriter?.StartBuffering();
            await FullRescanAsync(CancellationToken.None).ConfigureAwait(false);
            await (_propertyWriter?.LoadInitialStateAndResumeAsync(CancellationToken.None)
                ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _connectionManager.LogFirstOccurrence("Rescan", exception, "Full rescan failed.");
        }
    }

    #endregion

    #region Health Check Loop (ExecuteAsync)

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Health check loop. Connection monitoring and reconnection are handled by
        // AdsSession resurrection and event handlers (ConnectionStateChanged, AdsStateChanged,
        // AdsSymbolVersionChanged). This loop serves as a periodic heartbeat for future extensions.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_configuration.HealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _connectionManager.LogFirstOccurrence("HealthCheck", exception, "Health check failed.");
            }
        }
    }

    #endregion

    #region IAdsClientDiagnosticsSource Implementation

    /// <summary>
    /// Gets diagnostic information about the client connection state.
    /// </summary>
    public AdsClientDiagnostics Diagnostics => _diagnostics ??= new AdsClientDiagnostics(this);

    AdsState? IAdsClientDiagnosticsSource.CurrentState => _connectionManager.CurrentAdsState;

    bool IAdsClientDiagnosticsSource.IsConnected => _connectionManager.IsConnected;

    int IAdsClientDiagnosticsSource.NotificationCount => _subscriptionManager.NotificationCount;

    int IAdsClientDiagnosticsSource.PolledCount => _subscriptionManager.PolledCount;

    long IAdsClientDiagnosticsSource.TotalReconnectionAttempts => _connectionManager.TotalReconnectionAttempts;

    long IAdsClientDiagnosticsSource.SuccessfulReconnections => _connectionManager.SuccessfulReconnections;

    long IAdsClientDiagnosticsSource.FailedReconnections => _connectionManager.FailedReconnections;

    DateTimeOffset? IAdsClientDiagnosticsSource.LastConnectedAt => _connectionManager.LastConnectedAt;

    bool IAdsClientDiagnosticsSource.IsCircuitBreakerOpen => _connectionManager.IsCircuitBreakerOpen;

    long IAdsClientDiagnosticsSource.CircuitBreakerTripCount => _connectionManager.CircuitBreakerTripCount;

    #endregion

    #region Dispose

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
        Dispose();
    }

    #endregion
}
