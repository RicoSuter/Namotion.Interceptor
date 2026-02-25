using System.Collections.Concurrent;
using System.Reactive.Disposables;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Resilience;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.Reactive;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Connects a subject graph to a Beckhoff TwinCAT PLC via ADS protocol.
/// Manages connection lifecycle, notification subscriptions, batch polling, and bidirectional synchronization.
/// </summary>
internal sealed class TwinCatSubjectClientSource : BackgroundService, ISubjectSource, IAdsClientDiagnosticsSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly AdsSubjectLoader _subjectLoader;

    // ADS connection objects
    private AdsSession? _session;
    private IAdsConnection? _connection;
    private ISymbolLoader? _symbolLoader;
    private SubjectPropertyWriter? _propertyWriter;

    // Caches keyed by PropertyReference (stable) not RegisteredSubjectProperty (can become stale)
    private readonly ConcurrentDictionary<string, PropertyReference?> _symbolToProperty = new();
    private readonly ConcurrentDictionary<PropertyReference, string> _propertyToSymbol
        = new(PropertyReference.Comparer);
    private readonly ConcurrentDictionary<PropertyReference, IDisposable> _notificationSubscriptions
        = new(PropertyReference.Comparer);
    private readonly ConcurrentDictionary<PropertyReference, string> _polledProperties
        = new(PropertyReference.Comparer);
    private volatile bool _pollingCollectionDirty;
    private readonly CompositeDisposable _subscriptions = new();

    // First-occurrence logging state (Warning first, then Debug until cleared)
    private readonly ConcurrentDictionary<string, bool> _loggedErrors = new();

    // Diagnostics tracking
#pragma warning disable CS0414 // Field assigned but not read — used for diagnostics/future health checks
    private volatile bool _isStarted;
#pragma warning restore CS0414
    private long _totalReconnectionAttempts;
    private long _successfulReconnections;
    private long _failedReconnections;
    private long _lastConnectedAtTicks;
    private AdsClientDiagnostics? _diagnostics;
    private int _currentAdsState = -1; // -1 = not set, otherwise cast to AdsState
    private AdsState? _previousAdsState;
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

        _circuitBreaker = new CircuitBreaker(
            configuration.CircuitBreakerFailureThreshold,
            configuration.CircuitBreakerCooldown);

        _subjectLoader = new AdsSubjectLoader(configuration.PathProvider);

        _ownership = new SourceOwnershipManager(
            this,
            onReleasing: OnPropertyReleasing,
            onSubjectDetaching: OnSubjectDetaching);
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

        await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
        await FullRescanAsync(cancellationToken).ConfigureAwait(false);

        _isStarted = true;
        return _subscriptions;
    }

    /// <inheritdoc />
    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
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

            var symbolPath = GetSymbolPath(propertyReference);
            if (symbolPath is null)
            {
                continue;
            }

            var symbol = TryGetSymbol(symbolPath);
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
        var connection = _connection;
        if (connection is null)
        {
            return new ValueTask<WriteResult>(
                WriteResult.Failure(changes, new InvalidOperationException("ADS connection is not established.")));
        }

        try
        {
            var symbols = new List<ISymbol>();
            var values = new List<object>();
            var validChanges = new List<SubjectPropertyChange>();

            foreach (var change in changes.Span)
            {
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is null)
                {
                    continue;
                }

                var symbolPath = GetSymbolPath(change.Property);
                if (symbolPath is null)
                {
                    continue;
                }

                var symbol = TryGetSymbol(symbolPath);
                if (symbol is null)
                {
                    continue;
                }

                symbols.Add(symbol);
                var convertedValue = _configuration.ValueConverter.ConvertToAdsValue(
                    change.GetNewValue<object?>(), registeredProperty);
                values.Add(convertedValue ?? DBNull.Value);
                validChanges.Add(change);
            }

            if (symbols.Count == 0)
            {
                return new ValueTask<WriteResult>(WriteResult.Success);
            }

            var sumWrite = new SumSymbolWrite(connection, symbols);
            var writeResult = sumWrite.TryWrite(values.ToArray(), out var errorCodes);

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

    #region Connection Management

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_circuitBreaker.ShouldAttempt())
                {
                    Interlocked.Increment(ref _totalReconnectionAttempts);

                    var amsNetId = AmsNetId.Parse(_configuration.AmsNetId);
                    var amsAddress = new AmsAddress(amsNetId, _configuration.AmsPort);

                    _session = _configuration.SessionSettings is not null
                        ? new AdsSession(amsAddress, _configuration.SessionSettings)
                        : new AdsSession(amsAddress);

                    var sessionConnection = _session.Connect();
                    _connection = _session.Connection as IAdsConnection;

                    // Subscribe to connection state changes
                    if (_connection is AdsConnection adsConnection)
                    {
                        adsConnection.ConnectionStateChanged += OnConnectionStateChanged;
                    }

                    // Subscribe to ADS state changes via the underlying client
                    if (_connection is AdsClient adsClient)
                    {
                        adsClient.AdsStateChanged += OnAdsStateChanged;
                        adsClient.AdsSymbolVersionChanged += OnSymbolVersionChanged;
                    }

                    Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    _circuitBreaker.RecordSuccess();
                    _logger.LogInformation(
                        "Connected to TwinCAT PLC at {AmsNetId}:{Port}.",
                        _configuration.AmsNetId, _configuration.AmsPort);
                    return;
                }
                else
                {
                    _logger.LogDebug(
                        "Circuit breaker open, skipping connection attempt. Cooldown remaining: {Remaining}",
                        _circuitBreaker.GetCooldownRemaining());
                }
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedReconnections);
                LogFirstOccurrence("Connection", exception,
                    "Failed to connect to TwinCAT PLC. Retrying...");
                _circuitBreaker.RecordFailure();
            }

            await Task.Delay(_configuration.HealthCheckInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion

    #region Full Rescan

    private Task FullRescanAsync(CancellationToken cancellationToken)
    {
        // Mark polling dirty before clearing to guard against in-flight timer callbacks
        _pollingCollectionDirty = true;

        // Dispose existing subscriptions (CompositeDisposable.Clear disposes contained items)
        _subscriptions.Clear();
        _symbolToProperty.Clear();
        _propertyToSymbol.Clear();
        _notificationSubscriptions.Clear();
        _polledProperties.Clear();

        // Load symbol loader from session
        if (_session is not null)
        {
            _symbolLoader = SymbolLoaderFactory.Create(
                _connection!,
                new SymbolLoaderSettings(SymbolsLoadMode.DynamicTree));
        }

        // Load subject graph
        var graphMappings = _subjectLoader.LoadSubjectGraph(_subject);

        // Determine which properties get notifications vs polling
        var effectiveModes = DetermineEffectiveReadModes(graphMappings);

        var polledSymbols = new List<(RegisteredSubjectProperty Property, string SymbolPath)>();

        foreach (var (property, symbolPath, effectiveMode) in effectiveModes)
        {
            if (!_ownership.ClaimSource(property.Reference))
            {
                continue;
            }

            // Register in bidirectional symbol-to-property lookups
            _symbolToProperty[symbolPath] = property.Reference;
            _propertyToSymbol[property.Reference] = symbolPath;

            if (effectiveMode == AdsReadMode.Notification)
            {
                RegisterNotification(property, symbolPath);
            }
            else
            {
                polledSymbols.Add((property, symbolPath));
                _polledProperties[property.Reference] = symbolPath;
            }
        }

        if (polledSymbols.Count > 0)
        {
            StartBatchPolling(polledSymbols);
        }

        _logger.LogInformation(
            "Registered {NotificationCount} notification and {PolledCount} polled variables.",
            _notificationSubscriptions.Count, _polledProperties.Count);

        return Task.CompletedTask;
    }

    #endregion

    #region Read Mode Demotion

    /// <summary>
    /// Determines the effective read mode for each property, applying the two-pass auto-demotion algorithm.
    /// Notification mode properties are never demoted. Auto mode properties are demoted to polling
    /// when the MaxNotifications limit is exceeded, with higher Priority values demoted first,
    /// then higher CycleTime as tiebreaker.
    /// </summary>
    /// <param name="mappings">The property-to-symbol mappings from the subject graph.</param>
    /// <param name="defaultReadMode">The default read mode for properties without explicit configuration.</param>
    /// <param name="defaultCycleTime">The default notification cycle time in milliseconds.</param>
    /// <param name="maxNotifications">The maximum number of concurrent notifications before demotion.</param>
    /// <returns>A list of tuples with property, symbol path, and effective read mode.</returns>
    internal static IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsReadMode EffectiveMode)>
        DetermineEffectiveReadModes(
            IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath)> mappings,
            AdsReadMode defaultReadMode,
            int defaultCycleTime,
            int maxNotifications)
    {
        var result = new List<(RegisteredSubjectProperty, string, AdsReadMode)>(mappings.Count);

        // Pass 1: Collect all properties with their configured read modes
        var notificationCount = 0;
        var autoModeEntries = new List<(int Index, int Priority, int CycleTime)>();

        for (var index = 0; index < mappings.Count; index++)
        {
            var (property, symbolPath) = mappings[index];
            var readMode = GetConfiguredReadMode(property, defaultReadMode);
            var cycleTime = GetConfiguredCycleTime(property, defaultCycleTime);
            var priority = GetConfiguredPriority(property);

            if (readMode == AdsReadMode.Notification)
            {
                // Protected - always notification
                result.Add((property, symbolPath, AdsReadMode.Notification));
                notificationCount++;
            }
            else if (readMode == AdsReadMode.Polled)
            {
                // Always polled
                result.Add((property, symbolPath, AdsReadMode.Polled));
            }
            else
            {
                // Auto - starts as notification, may be demoted
                result.Add((property, symbolPath, AdsReadMode.Notification));
                autoModeEntries.Add((index, priority, cycleTime));
                notificationCount++;
            }
        }

        // Pass 2: Demote Auto properties if over the limit
        if (notificationCount > maxNotifications)
        {
            var excessCount = notificationCount - maxNotifications;

            // Sort by Priority descending (higher demoted first), then CycleTime descending (slower demoted first)
            var sortedAutoEntries = autoModeEntries
                .OrderByDescending(entry => entry.Priority)
                .ThenByDescending(entry => entry.CycleTime)
                .ToList();

            var demotionCount = Math.Min(excessCount, sortedAutoEntries.Count);
            for (var index = 0; index < demotionCount; index++)
            {
                var entryIndex = sortedAutoEntries[index].Index;
                var original = result[entryIndex];
                result[entryIndex] = (original.Item1, original.Item2, AdsReadMode.Polled);
            }
        }

        return result;
    }

    private IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsReadMode EffectiveMode)>
        DetermineEffectiveReadModes(
            IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath)> mappings)
    {
        return DetermineEffectiveReadModes(
            mappings,
            _configuration.DefaultReadMode,
            _configuration.DefaultCycleTime,
            _configuration.MaxNotifications);
    }

    internal static AdsReadMode GetConfiguredReadMode(RegisteredSubjectProperty property, AdsReadMode defaultReadMode)
    {
        var attribute = property.ReflectionAttributes.OfType<AdsVariableAttribute>().FirstOrDefault();
        if (attribute is not null && attribute.ReadMode != AdsReadMode.Auto)
        {
            return attribute.ReadMode;
        }

        return defaultReadMode;
    }

    internal static int GetConfiguredCycleTime(RegisteredSubjectProperty property, int defaultCycleTime)
    {
        var attribute = property.ReflectionAttributes.OfType<AdsVariableAttribute>().FirstOrDefault();
        if (attribute is not null && attribute.CycleTime != int.MinValue)
        {
            return attribute.CycleTime;
        }

        return defaultCycleTime;
    }

    internal static int GetConfiguredPriority(RegisteredSubjectProperty property)
    {
        var attribute = property.ReflectionAttributes.OfType<AdsVariableAttribute>().FirstOrDefault();
        return attribute?.Priority ?? 0;
    }

    #endregion

    #region Notification Registration

    private void RegisterNotification(RegisteredSubjectProperty property, string symbolPath)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        var symbol = TryGetSymbol(symbolPath);
        if (symbol is null)
        {
            LogFirstOccurrence($"Symbol:{symbolPath}", null,
                "Symbol '{SymbolPath}' not found in PLC. Skipping notification.", symbolPath);
            return;
        }

        var cycleTime = GetConfiguredCycleTime(property, _configuration.DefaultCycleTime);
        var maxDelay = GetConfiguredMaxDelay(property, _configuration.DefaultMaxDelay);
        var notificationSettings = new NotificationSettings(
            AdsTransMode.OnChange, cycleTime, maxDelay);

        var propertyReference = property.Reference;
        var subscription = connection
            .WhenNotification(symbol, notificationSettings)
            .Subscribe(symbolValue => OnValueReceived(propertyReference, symbolValue));

        _notificationSubscriptions[propertyReference] = subscription;
        _subscriptions.Add(subscription);
    }

    private static int GetConfiguredMaxDelay(RegisteredSubjectProperty property, int defaultMaxDelay)
    {
        var attribute = property.ReflectionAttributes.OfType<AdsVariableAttribute>().FirstOrDefault();
        if (attribute is not null && attribute.MaxDelay != int.MinValue)
        {
            return attribute.MaxDelay;
        }

        return defaultMaxDelay;
    }

    #endregion

    #region Batch Polling

    private void StartBatchPolling(List<(RegisteredSubjectProperty Property, string SymbolPath)> polledSymbols)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        var symbols = new List<ISymbol>();
        var validEntries = new List<(RegisteredSubjectProperty Property, string SymbolPath)>();

        foreach (var (property, symbolPath) in polledSymbols)
        {
            var symbol = TryGetSymbol(symbolPath);
            if (symbol is not null)
            {
                symbols.Add(symbol);
                validEntries.Add((property, symbolPath));
            }
        }

        if (symbols.Count == 0)
        {
            return;
        }

        var timer = new Timer(_ =>
        {
            try
            {
                if (_pollingCollectionDirty)
                {
                    // Polling collection has changed, skip this cycle
                    // A full rescan will rebuild it
                    return;
                }

                var sumRead = new SumSymbolRead(connection, symbols);
                var readResult = sumRead.TryRead(out var values, out var errorCodes);

                if (readResult != AdsErrorCode.NoError || values is null)
                {
                    LogFirstOccurrence("BatchPoll", null, "Batch polling failed with error: {ErrorCode}", readResult);
                    return;
                }

                for (var index = 0; index < validEntries.Count && index < values.Length; index++)
                {
                    if (errorCodes is not null && errorCodes[index] != AdsErrorCode.NoError)
                    {
                        continue;
                    }

                    // Pass raw ADS value — OnValueReceived handles conversion
                    OnValueReceived(validEntries[index].Property.Reference, values[index]);
                }
            }
            catch (Exception exception)
            {
                LogFirstOccurrence("BatchPoll", exception, "Batch polling failed.");
            }
        }, null, TimeSpan.Zero, _configuration.PollingInterval);

        _subscriptions.Add(Disposable.Create(() => timer.Dispose()));
    }

    #endregion

    #region Value Processing

    private void OnValueReceived(PropertyReference propertyReference, object? adsValue)
    {
        var registeredProperty = propertyReference.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return; // Subject was detached, skip
        }

        var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(adsValue, registeredProperty);

        _propertyWriter?.Write(
            (propertyReference, convertedValue, this),
            static state => state.propertyReference.SetValueFromSource(
                state.Item3,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                state.convertedValue));
    }

    #endregion

    #region Event Handlers

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs eventArgs)
    {
        if (eventArgs.NewState == ConnectionState.Connected &&
            eventArgs.OldState != ConnectionState.Connected)
        {
            _logger.LogInformation("ADS connection restored. Triggering full rescan.");
            Interlocked.Increment(ref _successfulReconnections);
            _ = TriggerFullRescanAsync();
        }
        else if (eventArgs.NewState != ConnectionState.Connected)
        {
            _propertyWriter?.StartBuffering();
            ClearFirstOccurrenceLog("Connection");
        }
    }

    private void OnAdsStateChanged(object? sender, AdsStateChangedEventArgs eventArgs)
    {
        var newState = eventArgs.State.AdsState;
        Volatile.Write(ref _currentAdsState, (int)newState);

        if (newState == AdsState.Run && _previousAdsState != AdsState.Run)
        {
            _logger.LogInformation("PLC entered Run state. Triggering full rescan.");
            _ = TriggerFullRescanAsync();
        }
        else if (newState != AdsState.Run)
        {
            LogFirstOccurrence("PlcState", null,
                "PLC left Run state: {State}. Writes paused.", newState);
        }

        _previousAdsState = newState;
    }

    private void OnSymbolVersionChanged(object? sender, AdsSymbolVersionChangedEventArgs eventArgs)
    {
        _logger.LogInformation("Symbol version changed. Triggering full rescan.");
        _ = TriggerFullRescanAsync();
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
            LogFirstOccurrence("Rescan", exception, "Full rescan failed.");
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
                LogFirstOccurrence("HealthCheck", exception, "Health check failed.");
            }
        }
    }

    #endregion

    #region Cleanup Callbacks

    private void OnPropertyReleasing(PropertyReference property)
    {
        // 1. Dispose ADS notification subscription for this property
        if (_notificationSubscriptions.TryRemove(property, out var subscription))
        {
            subscription.Dispose();
        }

        // 2. Remove from batch polling collection
        if (_polledProperties.TryRemove(property, out _))
        {
            _pollingCollectionDirty = true;
        }

        // 3. Remove from symbol-to-property lookups
        if (_propertyToSymbol.TryRemove(property, out var removedSymbolPath))
        {
            _symbolToProperty.TryRemove(removedSymbolPath, out _);
        }
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
        // Bulk cleanup: remove all symbol cache entries for the detached subject
        foreach (var kvp in _symbolToProperty)
        {
            if (kvp.Value.HasValue && kvp.Value.Value.Subject == subject)
            {
                _symbolToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }

    #endregion

    #region First-Occurrence Logging

    private void LogFirstOccurrence(string category, Exception? exception, string message, params object[] arguments)
    {
        if (_loggedErrors.TryAdd(category, true))
        {
            if (exception is not null)
            {
                _logger.LogWarning(exception, message, arguments);
            }
            else
            {
                _logger.LogWarning(message, arguments);
            }
        }
        else
        {
            if (exception is not null)
            {
                _logger.LogDebug(exception, message, arguments);
            }
            else
            {
                _logger.LogDebug(message, arguments);
            }
        }
    }

    private void ClearFirstOccurrenceLog(string category)
    {
        _loggedErrors.TryRemove(category, out _);
    }

    #endregion

    #region Symbol Helpers

    private string? GetSymbolPath(PropertyReference propertyReference)
    {
        return _propertyToSymbol.TryGetValue(propertyReference, out var symbolPath)
            ? symbolPath
            : null;
    }

    private ISymbol? TryGetSymbol(string symbolPath)
    {
        if (_symbolLoader is null)
        {
            return null;
        }

        try
        {
            if (_symbolLoader.Symbols.TryGetInstance(symbolPath, out var symbol))
            {
                return symbol;
            }
        }
        catch (Exception)
        {
            // Symbol not found or loader error
        }

        return null;
    }

    #endregion

    #region IAdsClientDiagnosticsSource Implementation

    /// <summary>
    /// Gets diagnostic information about the client connection state.
    /// </summary>
    public AdsClientDiagnostics Diagnostics => _diagnostics ??= new AdsClientDiagnostics(this);

    AdsState? IAdsClientDiagnosticsSource.CurrentState
    {
        get
        {
            var value = Volatile.Read(ref _currentAdsState);
            return value == -1 ? null : (AdsState)value;
        }
    }

    bool IAdsClientDiagnosticsSource.IsConnected =>
        _connection is AdsClient client && client.IsConnected;

    int IAdsClientDiagnosticsSource.NotificationCount => _notificationSubscriptions.Count;

    int IAdsClientDiagnosticsSource.PolledCount => _polledProperties.Count;

    long IAdsClientDiagnosticsSource.TotalReconnectionAttempts =>
        Interlocked.Read(ref _totalReconnectionAttempts);

    long IAdsClientDiagnosticsSource.SuccessfulReconnections =>
        Interlocked.Read(ref _successfulReconnections);

    long IAdsClientDiagnosticsSource.FailedReconnections =>
        Interlocked.Read(ref _failedReconnections);

    DateTimeOffset? IAdsClientDiagnosticsSource.LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    bool IAdsClientDiagnosticsSource.IsCircuitBreakerOpen => _circuitBreaker.IsOpen;

    long IAdsClientDiagnosticsSource.CircuitBreakerTripCount => _circuitBreaker.TripCount;

    #endregion

    #region Dispose

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _subscriptions.Dispose();

        // Unsubscribe from events
        if (_connection is AdsConnection adsConnection)
        {
            adsConnection.ConnectionStateChanged -= OnConnectionStateChanged;
        }

        if (_connection is AdsClient adsClient)
        {
            adsClient.AdsStateChanged -= OnAdsStateChanged;
            adsClient.AdsSymbolVersionChanged -= OnSymbolVersionChanged;
        }

        // Dispose session (which disposes connection)
        if (_session is not null)
        {
            try
            {
                _session.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Error disposing ADS session.");
            }

            _session = null;
            _connection = null;
        }

        _symbolLoader = null;
        _ownership.Dispose();
        Dispose();
    }

    #endregion
}
