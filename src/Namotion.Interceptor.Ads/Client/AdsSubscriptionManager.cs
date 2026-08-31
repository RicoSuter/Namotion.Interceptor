using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.Reactive;
using TwinCAT.Ads.SumCommand;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Ads.Client;

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
    /// <summary>Properties served by a notification, mapped to their symbol path. Holds no
    /// disposable: one subscription now serves a whole settings group, so it is owned by
    /// <see cref="_subscriptions"/> and torn down as a unit.</summary>
    private readonly ConcurrentDictionary<PropertyReference, string> _notificationProperties
        = new(PropertyReference.Comparer);
    private readonly ConcurrentDictionary<PropertyReference, string> _polledProperties
        = new(PropertyReference.Comparer);
    /// <summary>Bumped on every change to <see cref="_polledProperties"/>. Compared against
    /// <see cref="_pollingSnapshotVersion"/> rather than carried as a boolean, so a registration that
    /// lands while a rebuild is running is not erased by that rebuild clearing the flag afterwards.</summary>
    private int _pollingCollectionVersion;
    private int _pollingSnapshotVersion;

    /// <summary>Symbol paths whose PLC type will not resolve (enums), learned on first failed read.
    /// Keyed by path so a rescan keeps what was learned. Concurrent: polling reads in parallel.</summary>
    private readonly ConcurrentDictionary<string, byte> _rawIntegerSymbols = new();

    /// <summary>Handles for <see cref="_rawIntegerSymbols"/>, so polling does not create one per
    /// cycle. Dropped on a failed read to replace handles invalidated by a reconnect.</summary>
    private readonly ConcurrentDictionary<string, uint> _rawIntegerHandles = new();

    /// <summary>What the individual-read poll passes record, surfaced on the client diagnostics.</summary>
    internal AdsPollingMetrics PollingMetrics { get; } = new();

    // Polling snapshot — swapped atomically via volatile reference to avoid torn reads.
    // Only the polling thread mutates UseFallback; all other fields are set once during construction.
    private volatile PollingSnapshot _pollingSnapshot = PollingSnapshot.Empty;

    private sealed class PollingSnapshot
    {
        public static readonly PollingSnapshot Empty = new([], [], null);

        public readonly List<ISymbol> Symbols;
        public readonly List<(PropertyReference Reference, string SymbolPath)> Entries;
        public readonly SumSymbolRead? SumRead;
        public volatile bool UseFallback; // only mutated by the polling thread

        public PollingSnapshot(List<ISymbol> symbols, List<(PropertyReference, string)> entries, SumSymbolRead? sumRead)
        {
            Symbols = symbols;
            Entries = entries;
            SumRead = sumRead;
        }
    }

    /// <summary>
    /// Gets whether the polling collection has been marked dirty (for testing).
    /// </summary>
    internal bool IsPollingCollectionDirty =>
        Volatile.Read(ref _pollingCollectionVersion) != Volatile.Read(ref _pollingSnapshotVersion);
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
    internal int NotificationCount => _notificationProperties.Count;

    /// <summary>Number of live notification subscriptions, one per settings group rather than one
    /// per property. Exposed so a test can assert the grouping actually happens.</summary>
    internal int NotificationSubscriptionCount => Volatile.Read(ref _notificationSubscriptionCount);

    private int _notificationSubscriptionCount;

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
        IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsPropertyMapping Mapping)> mappings,
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

        var notificationGroups =
            new Dictionary<(int CycleTime, int MaxDelay), List<(PropertyReference Reference, string SymbolPath, ISymbol Symbol)>>();

        var mappingByReference = new Dictionary<PropertyReference, AdsPropertyMapping>(PropertyReference.Comparer);
        foreach (var (property, _, mapping) in mappings)
        {
            mappingByReference[property.Reference] = mapping;
        }

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
                var mapping = mappingByReference[property.Reference];
                var symbol = TryGetSymbol(symbolLoader, symbolPath);
                if (symbol is null)
                {
                    connectionManager.LogFirstOccurrence("SymbolNotFound", null,
                        "Symbol '{SymbolPath}' not found in PLC. Skipping notification.", symbolPath);
                    continue;
                }

                // Grouped by the settings the PLC actually needs, because one subscription is created
                // per group: WhenNotification allocates a dedicated EventLoopScheduler thread per call,
                // so subscribing per property costs one OS thread per property.
                var settingsKey = (
                    CycleTime: mapping.CycleTime ?? _configuration.DefaultCycleTime,
                    MaxDelay: mapping.MaxDelay ?? _configuration.DefaultMaxDelay);

                if (!notificationGroups.TryGetValue(settingsKey, out var group))
                {
                    group = [];
                    notificationGroups[settingsKey] = group;
                }

                group.Add((property.Reference, symbolPath, symbol));
            }
            else
            {
                _polledProperties[property.Reference] = symbolPath;
            }
        }

        foreach (var (settingsKey, group) in notificationGroups)
        {
            RegisterNotificationGroup(
                group, settingsKey.CycleTime, settingsKey.MaxDelay,
                connection, propertyWriter, source, connectionManager);
        }

        // Mark dirty so the next PollValuesAsync call rebuilds the polling snapshot
        Interlocked.Increment(ref _pollingCollectionVersion);

        _logger.LogInformation(
            "Registered {NotificationCount} notification and {PolledCount} polled variables.",
            _notificationProperties.Count, _polledProperties.Count);
    }

    /// <summary>
    /// Clears all caches, disposes subscriptions, and marks polling as dirty.
    /// </summary>
    internal void ClearAll()
    {
        // Mark polling dirty and clear snapshot so in-flight polls stop immediately
        Interlocked.Increment(ref _pollingCollectionVersion);
        _pollingSnapshot = PollingSnapshot.Empty;

        // Dispose existing subscriptions (CompositeDisposable.Clear disposes contained items)
        _subscriptions.Clear();
        _symbolToProperty.Clear();
        _propertyToSymbol.Clear();
        _notificationProperties.Clear();
        Volatile.Write(ref _notificationSubscriptionCount, 0);
        _polledProperties.Clear();
    }

    /// <summary>
    /// Gets the ADS symbol path for a property reference, or null if not cached.
    /// </summary>
    internal string? GetSymbolPath(PropertyReference propertyReference)
    {
        return _propertyToSymbol.GetValueOrDefault(propertyReference);
    }

    /// <summary>
    /// Adds a symbol-path mapping to the cache.
    /// </summary>
    internal void SetSymbolPath(PropertyReference propertyReference, string symbolPath)
    {
        _propertyToSymbol[propertyReference] = symbolPath;
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

    /// <summary>
    /// Cleanup callback for when a property is being released from ownership.
    /// </summary>
    internal void OnPropertyReleasing(PropertyReference property)
    {
        // 1. Stop treating this property as notification-backed. The subscription itself is shared
        // with the rest of its settings group, so it is not disposed here; a notification that still
        // arrives is dropped by OnValueReceived, which finds no registered property.
        _notificationProperties.TryRemove(property, out _);

        // 2. Remove from batch polling collection
        if (_polledProperties.TryRemove(property, out _))
        {
            Interlocked.Increment(ref _pollingCollectionVersion);
        }

        // 3. Remove from symbol-to-property lookups
        if (_propertyToSymbol.TryRemove(property, out var removedSymbolPath))
        {
            _symbolToProperty.TryRemove(removedSymbolPath, out _);
        }
    }

    /// <summary>
    /// Cleanup callback for when a subject is being detached from the graph.
    /// Called by SourceOwnershipManager before OnPropertyReleasing for each property.
    /// This eagerly removes _symbolToProperty entries; OnPropertyReleasing handles
    /// the remaining dictionaries (_notificationProperties, _polledProperties, _propertyToSymbol).
    /// </summary>
    internal void OnSubjectDetaching(IInterceptorSubject subject)
    {
        // TODO(perf): Consider a reverse lookup (subject → symbolPaths) to avoid O(n) scan
        // for large symbol sets. Currently acceptable because subject detachment is rare.
        foreach (var kvp in _symbolToProperty)
        {
            if (kvp.Value is { } reference && reference.Subject == subject)
            {
                _symbolToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Determines the effective read mode for each property, applying the two-pass auto-demotion algorithm.
    /// Notification mode properties are never demoted. Auto mode properties are demoted to polling
    /// when the MaxNotifications limit is exceeded, with higher Priority values demoted first,
    /// then higher CycleTime as tiebreaker.
    /// </summary>
    internal static IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsReadMode EffectiveMode)>
        DetermineEffectiveReadModes(
            IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsPropertyMapping Mapping)> mappings,
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
            var (property, symbolPath, mapping) = mappings[index];
            var readMode = mapping.ReadMode ?? defaultReadMode;
            var cycleTime = mapping.CycleTime ?? defaultCycleTime;
            var priority = mapping.Priority ?? 0;

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

    /// <summary>
    /// Registers one batched notification subscription for a group of symbols sharing the same
    /// notification settings.
    /// </summary>
    /// <remarks>
    /// One subscription per group rather than per property: the reactive extension allocates a
    /// dedicated <c>EventLoopScheduler</c>, and therefore an OS thread, for every
    /// <c>WhenNotification</c> call, so subscribing per property costs one thread per property and
    /// recreates them all on every rescan. Notifications are routed back to their property by symbol
    /// path, which is why <see cref="_symbolToProperty"/> is maintained.
    /// </remarks>
    private void RegisterNotificationGroup(
        List<(PropertyReference Reference, string SymbolPath, ISymbol Symbol)> group,
        int cycleTime,
        int maxDelay,
        IAdsConnection connection,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        AdsConnectionManager connectionManager)
    {
        var notificationSettings = new NotificationSettings(AdsTransMode.OnChange, cycleTime, maxDelay);
        var symbols = group.Select(entry => entry.Symbol).ToList();

        // Keyed by the instance path of the very symbols handed to WhenNotification, so the lookup
        // cannot disagree with what the notification echoes back.
        var routes = new Dictionary<string, (PropertyReference Reference, string SymbolPath)>(StringComparer.Ordinal);
        foreach (var entry in group)
        {
            routes[entry.Symbol.InstancePath] = (entry.Reference, entry.SymbolPath);
        }

        var subscription = connection
            .WhenNotification(symbols, notificationSettings)
            .Subscribe(
                notification =>
                {
                    if (!routes.TryGetValue(notification.Symbol.InstancePath, out var route))
                    {
                        return;
                    }

                    try
                    {
                        OnValueReceived(route.Reference, notification.Value, notification.TimeStamp, propertyWriter, source);
                    }
                    catch (Exception exception)
                    {
                        connectionManager.LogFirstOccurrence("NotificationCallback", exception,
                            "Failed to process notification for symbol '{SymbolPath}'.", route.SymbolPath);
                    }
                },
                exception =>
                {
                    // A PLC that refuses to register the notifications, because its own notification
                    // limit is reached or a symbol does not support one, reports it here rather than
                    // by throwing from Subscribe. Without this handler Rx swallows it, the
                    // subscription looks live, and the properties silently never update again.
                    connectionManager.LogFirstOccurrence("NotificationRegistration", exception,
                        "Notification group of {Count} symbols failed to register. Falling back to polling.",
                        group.Count);

                    foreach (var entry in group)
                    {
                        DemoteToPolling(entry.Reference, entry.SymbolPath);
                    }
                });

        foreach (var entry in group)
        {
            _notificationProperties[entry.Reference] = entry.SymbolPath;
        }

        _subscriptions.Add(subscription);
        Interlocked.Increment(ref _notificationSubscriptionCount);
    }

    /// <summary>
    /// Moves a property from notifications to polling after its notification failed, so it keeps
    /// updating rather than silently freezing at its last value.
    /// </summary>
    private void DemoteToPolling(PropertyReference propertyReference, string symbolPath)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        // The group's subscription has already terminated: onError completes the observable, so no
        // further notification arrives for any of its members.
        _notificationProperties.TryRemove(propertyReference, out _);

        _polledProperties[propertyReference] = symbolPath;
        Interlocked.Increment(ref _pollingCollectionVersion);
    }

    /// <summary>
    /// Performs a single polling cycle: reads all polled properties via batch or individual reads.
    /// Called periodically by the orchestrator's PeriodicTimer loop.
    /// </summary>
    /// <summary>
    /// Performs a single polling cycle: reads all polled properties via batch or individual reads.
    /// Called periodically by the orchestrator's PeriodicTimer loop.
    /// </summary>
    internal async Task PollValuesAsync(
        AdsConnectionManager connectionManager,
        SubjectPropertyWriter? propertyWriter,
        ISubjectSource source,
        CancellationToken cancellationToken)
    {
        if (connectionManager.Connection is null || _polledProperties.IsEmpty)
        {
            return;
        }

        if (IsPollingCollectionDirty)
        {
            RebuildPollingSnapshot(connectionManager);
        }

        // Read snapshot reference once — safe even if another thread swaps it via ClearAll/rebuild.
        var snapshot = _pollingSnapshot;
        if (snapshot.SumRead is null || snapshot.Symbols.Count == 0)
        {
            return;
        }

        if (!snapshot.UseFallback)
        {
            try
            {
                var readResult = await snapshot.SumRead.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (readResult.ErrorCode == AdsErrorCode.DeviceServiceNotSupported)
                {
                    _logger.LogDebug("SumSymbolRead not supported for polling, falling back to individual reads.");
                    snapshot.UseFallback = true;
                }
                else if (readResult.ErrorCode != AdsErrorCode.NoError || readResult.Values is null)
                {
                    connectionManager.LogFirstOccurrence("BatchPoll", null, "Batch polling failed with error: {ErrorCode}", readResult.ErrorCode);
                    return;
                }
                else
                {
                    var resultValues = readResult.Values;
                    var subErrors = readResult.SubErrors;
                    for (var index = 0; index < snapshot.Entries.Count && index < resultValues.Length; index++)
                    {
                        if (subErrors is not null && index < subErrors.Length && subErrors[index] != AdsErrorCode.NoError)
                        {
                            continue;
                        }

                        OnValueReceived(snapshot.Entries[index].Reference, resultValues[index], null, propertyWriter, source);
                    }

                    return;
                }
            }
            catch (AdsException exception) when (AdsErrorClassifier.GetErrorCode(exception) == AdsErrorCode.DeviceServiceNotSupported)
            {
                _logger.LogDebug("SumSymbolRead threw DeviceServiceNotSupported for polling, falling back to individual reads.");
                snapshot.UseFallback = true;
            }
        }

        // Individual read fallback. Sum commands cannot resolve these symbols, so the round trip
        // count is fixed and the only lever is how many are in flight. The work is IO, not CPU, so
        // every read is issued at once unless MaxConcurrentReads bounds it. Values are applied
        // afterwards on this thread: the property writer's threading contract is not ours to assume.
        var passStarted = Stopwatch.GetTimestamp();
        var readValues = new object?[snapshot.Symbols.Count];
        var failures = 0;

        async Task ReadIntoAsync(int index)
        {
            try
            {
                readValues[index] = await ReadPolledValueAsync(
                    connectionManager, snapshot, index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failures);
                connectionManager.LogFirstOccurrence("BatchPoll", exception,
                    "Failed to read polled symbol '{SymbolPath}'.", snapshot.Entries[index].SymbolPath);
            }
        }

        var maxConcurrentReads = _configuration.MaxConcurrentReads;
        if (maxConcurrentReads > 0)
        {
            await Parallel.ForAsync(0, snapshot.Symbols.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrentReads,
                    CancellationToken = cancellationToken,
                },
                async (index, _) => await ReadIntoAsync(index).ConfigureAwait(false)).ConfigureAwait(false);
        }
        else
        {
            var reads = new Task[snapshot.Symbols.Count];
            for (var index = 0; index < snapshot.Symbols.Count; index++)
            {
                reads[index] = ReadIntoAsync(index);
            }

            await Task.WhenAll(reads).ConfigureAwait(false);
        }

        for (var index = 0; index < readValues.Length; index++)
        {
            if (readValues[index] is not null)
            {
                OnValueReceived(snapshot.Entries[index].Reference, readValues[index], null, propertyWriter, source);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds;
        PollingMetrics.RecordPass(snapshot.Symbols.Count, elapsed, failures);
        _logger.LogDebug("Poll pass: {Count} symbols in {Elapsed:N0} ms, {Failures} failed.",
            snapshot.Symbols.Count, elapsed, failures);
    }

    /// <summary>
    /// Returns false when the symbol's PLC data type cannot be resolved by the TwinCAT type system
    /// (e.g. an enum whose definition was not loaded). Used to choose the raw integer read path.
    /// </summary>
    internal static bool IsDataTypeResolvable(ISymbol symbol)
    {
        try
        {
            return symbol.DataType is not null;
        }
        catch (CannotResolveDataTypeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one polled symbol, typed where the type system allows it and as a raw integer where it
    /// does not. Returns null when nothing should be published. Safe to call concurrently.
    /// </summary>
    private Task<object?> ReadPolledValueAsync(
        AdsConnectionManager connectionManager,
        PollingSnapshot snapshot,
        int index,
        CancellationToken cancellationToken)
    {
        return ReadSymbolValueAsync(
            connectionManager.Connection!,
            snapshot.Symbols[index],
            snapshot.Entries[index].SymbolPath,
            cancellationToken);
    }

    /// <summary>
    /// Reads one symbol, typed where the type system allows it and as a raw integer where it does
    /// not. Shared by the initial state load and by every polling pass, so a symbol whose PLC type
    /// will not resolve behaves the same at startup as it does later. Returns null when nothing
    /// should be published. Safe to call concurrently.
    /// </summary>
    internal async Task<object?> ReadSymbolValueAsync(
        IAdsConnection connection,
        ISymbol symbol,
        string symbolPath,
        CancellationToken cancellationToken)
    {
        // An enum throws while the value is built, not while its DataType is inspected, so it can
        // only be recognised by trying. Remembered on first failure so the doomed typed read
        // happens once, not every cycle.
        if (!_rawIntegerSymbols.ContainsKey(symbolPath))
        {
            try
            {
                var readResult = await ((IValueSymbol)symbol)
                    .ReadValueAsync(cancellationToken).ConfigureAwait(false);

                var readErrorCode = (AdsErrorCode)readResult.ErrorCode;
                if (readErrorCode != AdsErrorCode.NoError)
                {
                    // Surfaced rather than returned as null: a null is indistinguishable from
                    // "nothing to publish", so the caller would count a wholly failing poll pass
                    // as a clean one and report zero failed reads.
                    throw new AdsErrorException(
                        $"Failed to read ADS symbol '{symbolPath}'.", readErrorCode);
                }

                return readResult.Value;
            }
            catch (CannotResolveDataTypeException)
            {
                if (_rawIntegerSymbols.TryAdd(symbolPath, 0))
                {
                    _logger.LogDebug(
                        "Symbol '{SymbolPath}' has an unresolvable PLC type. Polling it as a raw integer.",
                        symbolPath);
                }
            }
        }

        // Underlying integer, as the notification side does; the converter rebuilds the enum.
        return await ReadRawIntegerAsync(
            connection, symbolPath,
            ((IBitSize)symbol).ByteSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a symbol whose PLC type will not resolve (an enum) as its underlying integer, picked
    /// by byte size. Null means the width has no integer counterpart. Signedness is irrelevant: the
    /// converter rebuilds the enum unchecked, so a UINT past 32767 such as Disabled still fits.
    /// </summary>
    private async Task<object?> ReadRawIntegerAsync(
        IAdsConnection connection,
        string symbolPath,
        int byteSize,
        CancellationToken cancellationToken)
    {
        // Not by instance path: that overload sizes its request buffer from T, so the path does not
        // fit. A handle carries it once, as the write side does.
        if (!_rawIntegerHandles.TryGetValue(symbolPath, out var handle))
        {
            var errorCode = connection.TryCreateVariableHandle(symbolPath, out handle);
            if (errorCode != AdsErrorCode.NoError)
            {
                throw new InvalidOperationException(
                    $"Failed to create ADS variable handle for '{symbolPath}': {errorCode}");
            }

            _rawIntegerHandles[symbolPath] = handle;
        }

        try
        {
            // ReadAnyAsync returns ResultValue<T>; unwrap it or the converter sees a non-integer
            // and passes it through untouched.
            var (value, errorCode) = byteSize switch
            {
                1 => Unwrap(await connection.ReadAnyAsync<byte>(handle, cancellationToken).ConfigureAwait(false)),
                2 => Unwrap(await connection.ReadAnyAsync<short>(handle, cancellationToken).ConfigureAwait(false)),
                4 => Unwrap(await connection.ReadAnyAsync<int>(handle, cancellationToken).ConfigureAwait(false)),
                8 => Unwrap(await connection.ReadAnyAsync<long>(handle, cancellationToken).ConfigureAwait(false)),
                _ => (null, AdsErrorCode.NoError),
            };

            if (errorCode != AdsErrorCode.NoError)
            {
                // The error arrives on the result, not as a throw, so this cannot be left to the
                // catch below. A handle does not survive a reconnect or a download, and a stale one
                // fails every cycle forever, so drop it and let the next pass create a new one.
                DropHandle(symbolPath);
                throw new AdsErrorException(
                    $"Failed to read ADS symbol '{symbolPath}' as a raw integer.", errorCode);
            }

            return value;

            static (object? Value, AdsErrorCode ErrorCode) Unwrap<T>(ResultValue<T> result) =>
                (result.ErrorCode == AdsErrorCode.NoError ? result.Value : null, result.ErrorCode);
        }
        catch
        {
            DropHandle(symbolPath);
            throw;
        }
    }

    /// <summary>
    /// Forgets a cached raw-integer handle so the next read creates a fresh one.
    /// </summary>
    private void DropHandle(string symbolPath)
    {
        _rawIntegerHandles.TryRemove(symbolPath, out _);
    }

    private void RebuildPollingSnapshot(AdsConnectionManager connectionManager)
    {
        var connection = connectionManager.Connection;
        // Read before building: anything registered while this runs advances the counter past it,
        // so the snapshot stays dirty and the next pass picks the new entries up.
        var version = Volatile.Read(ref _pollingCollectionVersion);

        if (connection is null)
        {
            _pollingSnapshot = PollingSnapshot.Empty;
            Volatile.Write(ref _pollingSnapshotVersion, version);
            return;
        }

        var newSymbols = new List<ISymbol>();
        var newEntries = new List<(PropertyReference Reference, string SymbolPath)>();

        foreach (var kvp in _polledProperties)
        {
            var symbol = TryGetSymbol(connectionManager.SymbolLoader, kvp.Value);
            if (symbol is not null)
            {
                newSymbols.Add(symbol);
                newEntries.Add((kvp.Key, kvp.Value));
            }
        }

        var sumRead = newSymbols.Count > 0
            ? new SumSymbolRead(connection, newSymbols)
            : null;

        // Assign snapshot atomically, then record the version it was built from.
        _pollingSnapshot = new PollingSnapshot(newSymbols, newEntries, sumRead);
        Volatile.Write(ref _pollingSnapshotVersion, version);
    }

    private void OnValueReceived(PropertyReference propertyReference, object? adsValue, DateTimeOffset? sourceTimestamp, SubjectPropertyWriter? propertyWriter, ISubjectSource source)
    {
        var registeredProperty = propertyReference.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return; // Subject was detached, skip
        }

        var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(adsValue, registeredProperty);

        propertyWriter?.Write(
            (propertyReference, convertedValue, source, sourceTimestamp),
            static state => state.propertyReference.SetValueFromSource(
                state.source,
                state.sourceTimestamp ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                state.convertedValue));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _subscriptions.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
