using System.Collections.Concurrent;
using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using TwinCAT.Ads;
using TwinCAT.Ads.Reactive;
using TwinCAT.Ads.SumCommand;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Manages ADS notification subscriptions, batch polling, symbol-property caches,
/// read mode demotion, and value processing.
/// </summary>
internal sealed class AdsSubscriptionManager : IAsyncDisposable
{
    private readonly AdsClientConfiguration _configuration;
    private readonly ILogger _logger;

    // Caches keyed by PropertyReference (stable) not RegisteredSubjectProperty (can become stale)
    private readonly ConcurrentDictionary<string, PropertyReference?> _symbolToProperty = new();
    private readonly ConcurrentDictionary<PropertyReference, string> _propertyToSymbol
        = new(PropertyReference.Comparer);
    private readonly ConcurrentDictionary<PropertyReference, IDisposable> _notificationSubscriptions
        = new(PropertyReference.Comparer);
    private readonly ConcurrentDictionary<PropertyReference, string> _polledProperties
        = new(PropertyReference.Comparer);
    private volatile bool _pollingCollectionDirty;

    /// <summary>
    /// Gets whether the polling collection has been marked dirty (for testing).
    /// </summary>
    internal bool IsPollingCollectionDirty => _pollingCollectionDirty;
    private readonly CompositeDisposable _subscriptions = new();

    private int _disposed; // 0 = false, 1 = true

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsSubscriptionManager"/> class.
    /// </summary>
    public AdsSubscriptionManager(AdsClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of properties with active notification subscriptions.
    /// </summary>
    internal int NotificationCount => _notificationSubscriptions.Count;

    /// <summary>
    /// Gets the number of properties using batch polling.
    /// </summary>
    internal int PolledCount => _polledProperties.Count;

    /// <summary>
    /// Gets the composite disposable containing all subscriptions (for orchestrator to return from StartListeningAsync).
    /// </summary>
    internal CompositeDisposable Subscriptions => _subscriptions;

    /// <summary>
    /// Registers notification subscriptions and batch polling for the given property-symbol mappings.
    /// </summary>
    internal void RegisterSubscriptions(
        IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath)> mappings,
        IAdsConnection connection,
        ISymbolLoader? symbolLoader,
        SourceOwnershipManager ownership,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        AdsConnectionManager connectionManager)
    {
        var effectiveModes = DetermineEffectiveReadModes(
            mappings,
            _configuration.DefaultReadMode,
            _configuration.DefaultCycleTime,
            _configuration.MaxNotifications);

        var polledSymbols = new List<(RegisteredSubjectProperty Property, string SymbolPath)>();

        foreach (var (property, symbolPath, effectiveMode) in effectiveModes)
        {
            if (!ownership.ClaimSource(property.Reference))
            {
                continue;
            }

            // Register in bidirectional symbol-to-property lookups
            _symbolToProperty[symbolPath] = property.Reference;
            _propertyToSymbol[property.Reference] = symbolPath;

            if (effectiveMode == AdsReadMode.Notification)
            {
                RegisterNotification(property, symbolPath, connection, symbolLoader, propertyWriter, source, connectionManager);
            }
            else
            {
                polledSymbols.Add((property, symbolPath));
                _polledProperties[property.Reference] = symbolPath;
            }
        }

        if (polledSymbols.Count > 0)
        {
            StartBatchPolling(polledSymbols, connection, symbolLoader, propertyWriter, source, connectionManager);
        }

        _logger.LogInformation(
            "Registered {NotificationCount} notification and {PolledCount} polled variables.",
            _notificationSubscriptions.Count, _polledProperties.Count);
    }

    /// <summary>
    /// Clears all caches, disposes subscriptions, and marks polling as dirty.
    /// </summary>
    internal void ClearAll()
    {
        // Mark polling dirty before clearing to guard against in-flight timer callbacks
        _pollingCollectionDirty = true;

        // Dispose existing subscriptions (CompositeDisposable.Clear disposes contained items)
        _subscriptions.Clear();
        _symbolToProperty.Clear();
        _propertyToSymbol.Clear();
        _notificationSubscriptions.Clear();
        _polledProperties.Clear();
    }

    /// <summary>
    /// Gets the ADS symbol path for a property reference, or null if not cached.
    /// </summary>
    internal string? GetSymbolPath(PropertyReference propertyReference)
    {
        return _propertyToSymbol.TryGetValue(propertyReference, out var symbolPath)
            ? symbolPath
            : null;
    }

    /// <summary>
    /// Tries to get an ADS symbol by path from the given symbol loader.
    /// </summary>
    internal static ISymbol? TryGetSymbol(ISymbolLoader? symbolLoader, string symbolPath)
    {
        if (symbolLoader is null)
        {
            return null;
        }

        try
        {
            if (symbolLoader.Symbols.TryGetInstance(symbolPath, out var symbol))
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

    #region Cleanup Callbacks

    /// <summary>
    /// Cleanup callback for when a property is being released from ownership.
    /// </summary>
    internal void OnPropertyReleasing(PropertyReference property)
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

    /// <summary>
    /// Cleanup callback for when a subject is being detached from the graph.
    /// </summary>
    internal void OnSubjectDetaching(IInterceptorSubject subject)
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

    #region Read Mode Demotion

    /// <summary>
    /// Determines the effective read mode for each property, applying the two-pass auto-demotion algorithm.
    /// Notification mode properties are never demoted. Auto mode properties are demoted to polling
    /// when the MaxNotifications limit is exceeded, with higher Priority values demoted first,
    /// then higher CycleTime as tiebreaker.
    /// </summary>
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

    internal static int GetConfiguredMaxDelay(RegisteredSubjectProperty property, int defaultMaxDelay)
    {
        var attribute = property.ReflectionAttributes.OfType<AdsVariableAttribute>().FirstOrDefault();
        if (attribute is not null && attribute.MaxDelay != int.MinValue)
        {
            return attribute.MaxDelay;
        }

        return defaultMaxDelay;
    }

    #endregion

    #region Notification Registration

    private void RegisterNotification(
        RegisteredSubjectProperty property,
        string symbolPath,
        IAdsConnection connection,
        ISymbolLoader? symbolLoader,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        AdsConnectionManager connectionManager)
    {
        var symbol = TryGetSymbol(symbolLoader, symbolPath);
        if (symbol is null)
        {
            connectionManager.LogFirstOccurrence($"Symbol:{symbolPath}", null,
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
            .Subscribe(symbolValue => OnValueReceived(propertyReference, symbolValue, propertyWriter, source));

        _notificationSubscriptions[propertyReference] = subscription;
        _subscriptions.Add(subscription);
    }

    #endregion

    #region Batch Polling

    private void StartBatchPolling(
        List<(RegisteredSubjectProperty Property, string SymbolPath)> polledSymbols,
        IAdsConnection connection,
        ISymbolLoader? symbolLoader,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        AdsConnectionManager connectionManager)
    {
        var symbols = new List<ISymbol>();
        var validEntries = new List<(RegisteredSubjectProperty Property, string SymbolPath)>();

        foreach (var (property, symbolPath) in polledSymbols)
        {
            var symbol = TryGetSymbol(symbolLoader, symbolPath);
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
                    connectionManager.LogFirstOccurrence("BatchPoll", null, "Batch polling failed with error: {ErrorCode}", readResult);
                    return;
                }

                for (var index = 0; index < validEntries.Count && index < values.Length; index++)
                {
                    if (errorCodes is not null && errorCodes[index] != AdsErrorCode.NoError)
                    {
                        continue;
                    }

                    // Pass raw ADS value — OnValueReceived handles conversion
                    OnValueReceived(validEntries[index].Property.Reference, values[index], propertyWriter, source);
                }
            }
            catch (Exception exception)
            {
                connectionManager.LogFirstOccurrence("BatchPoll", exception, "Batch polling failed.");
            }
        }, null, TimeSpan.Zero, _configuration.PollingInterval);

        _subscriptions.Add(Disposable.Create(() => timer.Dispose()));
    }

    #endregion

    #region Value Processing

    private void OnValueReceived(PropertyReference propertyReference, object? adsValue, SubjectPropertyWriter? propertyWriter, ISubjectSource source)
    {
        var registeredProperty = propertyReference.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return; // Subject was detached, skip
        }

        var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(adsValue, registeredProperty);

        propertyWriter?.Write(
            (propertyReference, convertedValue, source),
            static state => state.propertyReference.SetValueFromSource(
                state.source,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                state.convertedValue));
    }

    #endregion

    #region Dispose

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _subscriptions.Dispose();

        return ValueTask.CompletedTask;
    }

    #endregion
}
