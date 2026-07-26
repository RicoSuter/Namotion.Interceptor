using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// The graph-free, SQL-backed history engine. Operates only on canonical path strings and typed
/// values: partition-file management, schema, batched write plus periodic flush, raw queries,
/// look-back, coverage, and metrics. It implements <see cref="IHistoryStore"/> directly so a future
/// generic host can drive it without graph coupling; the <see cref="SqliteHistoryStoreSubject"/>
/// [InterceptorSubject] adapter delegates to it. Mirrors the value routing and point mapping of
/// <c>InMemoryHistoryStore</c> so query results are identical, but persists rows into
/// partitioned SQLite database files with <c>value_json</c> stored as TEXT.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore, IDisposable
{
    public const int DefaultMaxPendingSamples = 100_000;

    /// <summary>
    /// The aggregations every SQLite store path supports (the full set, independent of column type).
    /// </summary>
    public static readonly IReadOnlySet<string> AllAggregations = new HashSet<string>(StringComparer.Ordinal)
    {
        HistoryAggregations.Last,
        HistoryAggregations.First,
        HistoryAggregations.SampleAverage,
        HistoryAggregations.TimeWeightedAverage,
        HistoryAggregations.Minimum,
        HistoryAggregations.Maximum,
        HistoryAggregations.Sum,
        HistoryAggregations.Count,
        HistoryAggregations.StandardDeviation
    };

    private readonly int _priority;
    private readonly string _databaseDirectory;
    private readonly PartitionInterval _partitionInterval;
    private readonly TimeSpan _maxAge;
    private readonly int _maxJsonSize;
    private readonly int _maxPendingSamples;
    private readonly Func<DateTimeOffset> _getUtcNow;

    // Keep the existing moves.db filename for compatibility. It is now the metadata database and
    // contains both move records and coverage ranges. "moves" is never a valid partition key.
    private const string MetadataKey = "moves";

    private readonly object _pendingLock = new();
    private readonly object _flushLock = new();
    private readonly List<PendingSample> _pending = new();
    private readonly List<MoveRecord> _pendingMoves = new();
    private int _inFlightSampleCount;
    private DateTimeOffset? _earliestPendingTimestamp;
    private DateTimeOffset? _earliestInFlightTimestamp;
    private bool _droppingNewSamples;
    private bool _pendingStartsNewCoverageRange = true;
    private DateTimeOffset _pendingCoverageStart;
    private DateTimeOffset? _firstDroppedTimestamp;

    // The earliest instant this store holds an uncommitted (pending, in-flight, or dropped) change for,
    // as UTC ticks, or long.MaxValue when there is none. Written under _pendingLock, read without any
    // lock so coverage reads never wait behind a flush.
    private long _uncommittedFromTicks = long.MaxValue;

    private readonly object _connectionLock = new();
    private readonly Dictionary<string, SqliteConnection> _connections = new(StringComparer.Ordinal);
    private readonly SqliteCoverageStore _coverageStore;

    // The read/partition-layout context handed to SqliteHistoryReader. It captures this engine's
    // OpenPartition/OpenMetadata delegates (which take _connectionLock), so the reader runs entirely within
    // the engine's lock; the reader itself never locks and never touches _connections.
    private readonly SqliteReadContext _readContext;

    private long _recordedCount;
    private long _oversizeCount;
    private long _dropCount;

    private long _lastFlushUtcTicks;
    private volatile string? _lastError;
    private bool _reloadCoverageAfterFailure;

    public SqliteHistoryStore(
        int priority,
        string databaseDirectory,
        PartitionInterval partitionInterval,
        TimeSpan maxAge,
        int maxJsonSize,
        Func<DateTimeOffset> getUtcNow,
        int maxPendingSamples = DefaultMaxPendingSamples)
    {
        if (maxPendingSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPendingSamples), "The maximum pending sample count must be positive.");
        }

        _priority = priority;
        _databaseDirectory = databaseDirectory;
        _partitionInterval = partitionInterval;
        _maxAge = maxAge;
        _maxJsonSize = maxJsonSize;
        _maxPendingSamples = maxPendingSamples;
        _getUtcNow = getUtcNow;
        _pendingCoverageStart = getUtcNow();
        _readContext = new SqliteReadContext(
            _databaseDirectory, _partitionInterval, MetadataKey, OpenPartition, OpenMetadata);
        _coverageStore = new SqliteCoverageStore(OpenMetadata);

        Directory.CreateDirectory(_databaseDirectory);
        lock (_connectionLock)
        {
            _coverageStore.Reload();
        }
    }

    /// <summary>
    /// Restarts the coverage session at the current instant. The constructor already starts one, so
    /// this only narrows what the store claims: the owner calls it once its change subscription is
    /// live, so no change can fall inside claimed coverage without reaching this engine.
    /// </summary>
    internal void BeginCoverageSession()
    {
        lock (_pendingLock)
        {
            _pendingCoverageStart = _getUtcNow();
            _pendingStartsNewCoverageRange = true;
        }
    }

    public int Priority => _priority;

    public IReadOnlySet<string> SupportedAggregations => AllAggregations;

    public long RecordedCount => Interlocked.Read(ref _recordedCount);

    public long OversizeCount => Interlocked.Read(ref _oversizeCount);

    public long DropCount => Interlocked.Read(ref _dropCount);

    public int QueueDepth
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.Count + _inFlightSampleCount;
            }
        }
    }

    public DateTimeOffset? LastFlushUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastFlushUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public string? LastError => _lastError;

    public ImmutableArray<HistoryCoverage> CoverageRanges => GetEffectiveCoverageSnapshot();

    public long EstimatedStorageBytes
    {
        get
        {
            var total = 0L;
            if (Directory.Exists(_databaseDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_databaseDirectory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // best effort: a WAL/SHM file may vanish between enumeration and stat
                    }
                }
            }

            return total;
        }
    }

    public void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        TryRecord(propertyPath, timestamp, value, propertyType);
    }

    internal bool TryRecord(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        var column = HistoryColumns.GetValueColumnFor(propertyType);
        var isUlong = HistoryColumns.IsUlongProperty(propertyType);
        var routed = SqliteValueRouting.CreateRow(value, column, isUlong, _maxJsonSize);

        lock (_pendingLock)
        {
            if (_droppingNewSamples || _pending.Count + _inFlightSampleCount >= _maxPendingSamples)
            {
                _droppingNewSamples = true;
                _firstDroppedTimestamp = Earlier(_firstDroppedTimestamp, timestamp);
                Interlocked.Increment(ref _dropCount);
                PublishUncommittedWatermark();
                return false;
            }

            _pending.Add(new PendingSample(propertyPath, timestamp, routed.Row, column, isUlong));
            _earliestPendingTimestamp = Earlier(_earliestPendingTimestamp, timestamp);
            PublishUncommittedWatermark();
        }

        if (routed.Oversized)
        {
            Interlocked.Increment(ref _oversizeCount);
        }

        Interlocked.Increment(ref _recordedCount);
        return true;
    }

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
    {
        // Queue the move like a pending sample; FlushAsync persists it into the metadata database.
        lock (_pendingLock)
        {
            _pendingMoves.Add(new MoveRecord(timestamp, fromPath, toPath));
            _earliestPendingTimestamp = Earlier(_earliestPendingTimestamp, timestamp);
            PublishUncommittedWatermark();
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_flushLock)
        {
            PendingSample[] batch;
            MoveRecord[] moveBatch;
            bool startsNewCoverageRange;
            DateTimeOffset coverageStart;
            lock (_pendingLock)
            {
                batch = _pending.ToArray();
                _pending.Clear();
                moveBatch = _pendingMoves.ToArray();
                _pendingMoves.Clear();
                _earliestInFlightTimestamp = _earliestPendingTimestamp;
                _earliestPendingTimestamp = null;
                startsNewCoverageRange = _pendingStartsNewCoverageRange;
                coverageStart = _pendingCoverageStart;
                _pendingStartsNewCoverageRange = false;
                _inFlightSampleCount = batch.Length;
                PublishUncommittedWatermark();
            }

            try
            {
                var byPartition = new Dictionary<string, List<PendingSample>>(StringComparer.Ordinal);
                foreach (var sample in batch)
                {
                    var key = SqlitePartition.PartitionKey(sample.Timestamp, _partitionInterval);
                    if (!byPartition.TryGetValue(key, out var list))
                    {
                        list = new List<PendingSample>();
                        byPartition[key] = list;
                    }

                    list.Add(sample);
                }

                lock (_connectionLock)
                {
                    if (_reloadCoverageAfterFailure)
                    {
                        _coverageStore.Reload();
                        _reloadCoverageAfterFailure = false;
                    }

                    foreach (var (key, samples) in byPartition)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SqliteHistoryWriter.WritePartition(OpenPartition(key), samples);
                    }

                    if (moveBatch.Length > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SqliteHistoryWriter.WriteMoves(OpenMetadata(), moveBatch);
                    }
                }

                var successfulAt = _getUtcNow();
                lock (_pendingLock)
                {
                    var coverageEnd = successfulAt == DateTimeOffset.MaxValue
                        ? successfulAt
                        : successfulAt.AddTicks(1);

                    foreach (var sample in _pending)
                    {
                        if (sample.Timestamp < coverageEnd)
                        {
                            coverageEnd = sample.Timestamp;
                        }
                    }

                    foreach (var move in _pendingMoves)
                    {
                        if (move.Timestamp < coverageEnd)
                        {
                            coverageEnd = move.Timestamp;
                        }
                    }

                    var recoveredFromDrops = _droppingNewSamples && _pending.Count == 0;
                    if (recoveredFromDrops && _firstDroppedTimestamp is { } droppedAt &&
                        droppedAt < coverageEnd)
                    {
                        coverageEnd = droppedAt;
                    }

                    lock (_connectionLock)
                    {
                        var fromTicks = EpochTicks.ToEpochTicks(coverageStart);
                        var toTicks = EpochTicks.ToEpochTicks(coverageEnd);
                        if (fromTicks < toTicks)
                        {
                            _coverageStore.Update(fromTicks, toTicks, startsNewCoverageRange);
                        }
                    }

                    Interlocked.Exchange(ref _lastFlushUtcTicks, successfulAt.UtcTicks);
                    _lastError = null;

                    _inFlightSampleCount = 0;
                    _earliestInFlightTimestamp = null;
                    if (recoveredFromDrops)
                    {
                        _firstDroppedTimestamp = null;
                        _droppingNewSamples = false;
                        _pendingStartsNewCoverageRange = true;
                        _pendingCoverageStart = successfulAt;
                    }

                    PublishUncommittedWatermark();
                }
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                if (exception is not OperationCanceledException)
                {
                    lock (_connectionLock)
                    {
                        DisposeConnections();
                        _reloadCoverageAfterFailure = true;
                    }
                }

                // The write failed, so put the batch back at the front. Record() included the in-flight
                // count in its bound, so reinserting cannot exceed the pending sample limit.
                lock (_pendingLock)
                {
                    _inFlightSampleCount = 0;
                    _pending.InsertRange(0, batch);
                    _pendingMoves.InsertRange(0, moveBatch);
                    _earliestPendingTimestamp = Earlier(
                        _earliestPendingTimestamp,
                        _earliestInFlightTimestamp);
                    _earliestInFlightTimestamp = null;
                    _pendingStartsNewCoverageRange |= startsNewCoverageRange;
                    PublishUncommittedWatermark();
                }

                throw;
            }
        }

        return Task.CompletedTask;
    }

    private void DisposeConnections()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Close();
            connection.Dispose();
        }

        _connections.Clear();
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(query, cancellationToken));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HistoryPoint?>(GetSampleAtOrBefore(propertyPath, asOf));
    }

    public HistorySeries Query(HistoryQuery query) => Query(query, CancellationToken.None);

    private HistorySeries Query(HistoryQuery query, CancellationToken cancellationToken)
    {
        query.Validate();
        var coverageRanges = GetEffectiveCoverageSnapshot();

        // A single SqliteConnection/SqliteCommand is not thread-safe. Serialize all connection use
        // under the re-entrant _connectionLock so concurrent queries and the flush loop cannot collide
        // on shared cached connections. The reader runs entirely within this lock (it opens connections
        // through the engine's OpenPartition/OpenMoves delegates, which re-enter this lock).
        lock (_connectionLock)
        {
            // The bucket reader's TWA carry-seed look-back reads directly through the same context; it runs
            // while this lock is already held, matching the original inline GetSampleAtOrBefore look-back.
            var result = query.Bucket is null
                ? SqliteHistoryReader.QueryRaw(_readContext, query, cancellationToken)
                : SqliteBucketReader.QueryBucketed(
                    _readContext, query,
                    (path, asOf) => GetSampleAtOrBeforeCore(path, asOf, coverageRanges),
                    coverageRanges,
                    cancellationToken);
            return result with
            {
                CoverageRanges = HistoryCoverage.Clip(
                    coverageRanges, new HistoryCoverage(query.From, query.To))
            };
        }
    }

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        var coverageRanges = GetEffectiveCoverageSnapshot();

        // Serialize connection use under the re-entrant _connectionLock (see Query for the rationale).
        lock (_connectionLock)
        {
            return GetSampleAtOrBeforeCore(propertyPath, asOf, coverageRanges);
        }
    }

    private HistoryPoint? GetSampleAtOrBeforeCore(
        string propertyPath,
        DateTimeOffset asOf,
        ImmutableArray<HistoryCoverage>? coverageSnapshot = null)
    {
        var ranges = coverageSnapshot ?? _coverageStore.Snapshot;
        HistoryCoverage? containingRange = null;
        for (var index = ranges.Length - 1; index >= 0; index--)
        {
            var range = ranges[index];
            if (range.From <= asOf && range.To >= asOf)
            {
                containingRange = range;
                break;
            }
        }

        if (containingRange is not { } coverage)
        {
            return null;
        }

        var sample = SqliteHistoryReader.GetSampleAtOrBefore(_readContext, propertyPath, asOf);
        return sample is not null && sample.Timestamp >= coverage.From ? sample : null;
    }

    // The durable ranges never claim an instant this store still holds an uncommitted change for.
    // Both inputs are published independently and read without a lock: the durable snapshot changes
    // once per flush, and the watermark can only be read stale in the conservative direction (it is
    // read after the ranges, so a concurrent flush yields the older, narrower ranges).
    private ImmutableArray<HistoryCoverage> GetEffectiveCoverageSnapshot()
    {
        var ranges = _coverageStore.Snapshot;
        var uncommittedFromTicks = Interlocked.Read(ref _uncommittedFromTicks);
        if (uncommittedFromTicks == long.MaxValue)
        {
            return ranges;
        }

        var uncommittedFrom = new DateTimeOffset(uncommittedFromTicks, TimeSpan.Zero);
        var builder = ImmutableArray.CreateBuilder<HistoryCoverage>(ranges.Length);
        foreach (var range in ranges)
        {
            if (range.From >= uncommittedFrom)
            {
                break;
            }

            builder.Add(range.To > uncommittedFrom ? range with { To = uncommittedFrom } : range);
        }

        return builder.ToImmutable();
    }

    // Recomputes the earliest uncommitted instant and publishes it for lock-free coverage reads.
    // Must be called under _pendingLock whenever any of the three inputs changes.
    private void PublishUncommittedWatermark()
    {
        var uncommittedFrom = Earlier(
            Earlier(_firstDroppedTimestamp, _earliestPendingTimestamp),
            _earliestInFlightTimestamp);

        Interlocked.Exchange(
            ref _uncommittedFromTicks, uncommittedFrom?.UtcTicks ?? long.MaxValue);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        return right is not null && right < left ? right : left;
    }

    public void Sweep()
    {
        lock (_flushLock)
        {
            var cutoff = _getUtcNow() - _maxAge;

            lock (_pendingLock)
            {
                if (_pendingCoverageStart < cutoff)
                {
                    _pendingCoverageStart = cutoff;
                }
            }

            lock (_connectionLock)
            {
                foreach (var key in _readContext.EnumeratePartitionFileKeys().ToArray())
                {
                    var (_, end) = SqlitePartition.PartitionRange(key, _partitionInterval);
                    if (end < cutoff)
                    {
                        DeletePartition(key);
                    }
                }

                // Persist the WAL contents back into the surviving main database files so their on-disk
                // size reflects the data after the sweep.
                foreach (var connection in _connections.Values)
                {
                    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                }

                _coverageStore.Trim(EpochTicks.ToEpochTicks(cutoff));
            }
        }
    }

    // Closes and removes the cached connection (Windows holds WAL/SHM locks), then deletes the
    // partition's main file and its -wal/-shm siblings if present.
    private void DeletePartition(string key)
    {
        if (_connections.TryGetValue(key, out var connection))
        {
            connection.Close();
            connection.Dispose();
            _connections.Remove(key);
        }

        var path = _readContext.PartitionFilePath(key);
        DeleteFileIfExists(path);
        DeleteFileIfExists(path + "-wal");
        DeleteFileIfExists(path + "-shm");
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private SqliteConnection OpenPartition(string key)
    {
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _readContext.PartitionFilePath(key),
                Pooling = false
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection,
                "CREATE TABLE IF NOT EXISTS history (ts INTEGER NOT NULL, path TEXT NOT NULL, " +
                "value_long INTEGER, value_double REAL, value_json TEXT, PRIMARY KEY (path, ts)) WITHOUT ROWID;");
            Execute(connection,
                "CREATE TABLE IF NOT EXISTS path_meta (path TEXT PRIMARY KEY, column INTEGER NOT NULL, is_ulong INTEGER NOT NULL);");
            _connections[key] = connection;
            return connection;
        }
    }

    // Opens the small metadata database that stores move records and durable coverage ranges.
    private SqliteConnection OpenMetadata()
    {
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(MetadataKey, out var existing))
            {
                return existing;
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _readContext.PartitionFilePath(MetadataKey),
                Pooling = false
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection,
                "CREATE TABLE IF NOT EXISTS moves (ts INTEGER NOT NULL, from_path TEXT NOT NULL, to_path TEXT NOT NULL);");
            Execute(connection,
                "CREATE TABLE IF NOT EXISTS coverage_ranges (" +
                "id INTEGER PRIMARY KEY AUTOINCREMENT, from_ts INTEGER NOT NULL, to_ts INTEGER NOT NULL, " +
                "CHECK (from_ts < to_ts));");
            _connections[MetadataKey] = connection;
            return connection;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_connectionLock)
        {
            DisposeConnections();
        }
    }
}
