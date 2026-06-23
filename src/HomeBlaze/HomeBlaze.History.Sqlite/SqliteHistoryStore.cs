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
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly DateTimeOffset _startTime;

    // Fixed connection key for the moves database. It is never a valid partition key
    // (SqlitePartition.IsPartitionKey("moves", ...) is false), so partition enumeration and Sweep skip it.
    private const string MovesKey = "moves";

    private readonly object _pendingLock = new();
    private readonly List<PendingSample> _pending = new();
    private readonly List<MoveRecord> _pendingMoves = new();

    private readonly object _connectionLock = new();
    private readonly Dictionary<string, SqliteConnection> _connections = new(StringComparer.Ordinal);

    // The read/partition-layout context handed to SqliteHistoryReader. It captures this engine's
    // OpenPartition/OpenMoves delegates (which take _connectionLock), so the reader runs entirely within
    // the engine's lock; the reader itself never locks and never touches _connections.
    private readonly SqliteReadContext _readContext;

    private long _recordedCount;
    private long _oversizeCount;

    private long? _lastCommittedTicks;
    private long? _oldestCommittedTicks;
    private DateTimeOffset? _lastFlushUtc;
    private string? _lastError;

    public SqliteHistoryStore(
        int priority,
        string databaseDirectory,
        PartitionInterval partitionInterval,
        TimeSpan maxAge,
        int maxJsonSize,
        Func<DateTimeOffset> getUtcNow)
    {
        _priority = priority;
        _databaseDirectory = databaseDirectory;
        _partitionInterval = partitionInterval;
        _maxAge = maxAge;
        _maxJsonSize = maxJsonSize;
        _getUtcNow = getUtcNow;
        _startTime = getUtcNow();
        _readContext = new SqliteReadContext(
            _databaseDirectory, _partitionInterval, MovesKey, OpenPartition, OpenMoves);

        Directory.CreateDirectory(_databaseDirectory);
    }

    public int Priority => _priority;

    public IReadOnlySet<string> SupportedAggregations => AllAggregations;

    public long RecordedCount => Interlocked.Read(ref _recordedCount);

    public long OversizeCount => Interlocked.Read(ref _oversizeCount);

    public int QueueDepth
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.Count;
            }
        }
    }

    public DateTimeOffset? LastFlushUtc => _lastFlushUtc;

    public string? LastError => _lastError;

    public HistoryCoverage CurrentCoverage
    {
        get
        {
            // Before the first committed sample the store is empty: [now, now].
            if (_lastCommittedTicks is null)
            {
                var now = _getUtcNow();
                return new HistoryCoverage(now, now);
            }

            var from = _oldestCommittedTicks is { } oldest
                ? EpochTicks.FromEpochTicks(oldest)
                : EpochTicks.FromEpochTicks(_lastCommittedTicks.Value);
            return new HistoryCoverage(from, EpochTicks.FromEpochTicks(_lastCommittedTicks.Value));
        }
    }

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
        var column = HistoryColumns.GetValueColumnFor(propertyType);
        var isUlong = HistoryColumns.IsUlongProperty(propertyType);
        var routed = SqliteValueRouting.CreateRow(value, column, isUlong, _maxJsonSize);
        if (routed.Oversized)
        {
            Interlocked.Increment(ref _oversizeCount);
        }

        lock (_pendingLock)
        {
            _pending.Add(new PendingSample(propertyPath, timestamp, routed.Row, column, isUlong));
        }

        Interlocked.Increment(ref _recordedCount);
    }

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
    {
        // Queue the move like a pending sample; FlushAsync persists it into moves.db.
        lock (_pendingLock)
        {
            _pendingMoves.Add(new MoveRecord(timestamp, fromPath, toPath));
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        PendingSample[] batch;
        MoveRecord[] moveBatch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0 && _pendingMoves.Count == 0)
            {
                return Task.CompletedTask;
            }

            batch = _pending.ToArray();
            _pending.Clear();
            moveBatch = _pendingMoves.ToArray();
            _pendingMoves.Clear();
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

            // _pendingLock is already released here. Take the re-entrant _connectionLock for all
            // connection use (see Query). Lock ordering is always _pendingLock-then-_connectionLock
            // and never the reverse, so no cycle is possible. The body is synchronous, so nothing is
            // awaited while the lock is held.
            lock (_connectionLock)
            {
                var maxTicks = _lastCommittedTicks;
                foreach (var (key, samples) in byPartition)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var partitionMax = SqliteHistoryWriter.WritePartition(OpenPartition(key), samples);
                    if (maxTicks is null || partitionMax > maxTicks.Value)
                    {
                        maxTicks = partitionMax;
                    }
                }

                if (moveBatch.Length > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SqliteHistoryWriter.WriteMoves(OpenMoves(), moveBatch);
                }

                _lastCommittedTicks = maxTicks;
                RefreshOldestCommitted();
                _lastFlushUtc = _getUtcNow();
            }
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;

            // The write failed, so the batch was not persisted: put it back at the FRONT of the pending
            // lists (preserving order ahead of anything queued since) so the next flush retries it instead
            // of silently dropping data. _connectionLock has already been released here, so taking
            // _pendingLock now keeps the _pendingLock-then-_connectionLock ordering intact (never the reverse).
            lock (_pendingLock)
            {
                _pending.InsertRange(0, batch);
                _pendingMoves.InsertRange(0, moveBatch);
            }

            throw;
        }

        _lastError = null;
        return Task.CompletedTask;
    }

    private void RefreshOldestCommitted()
    {
        long? oldest = null;
        foreach (var key in _readContext.EnumeratePartitionFileKeys())
        {
            var connection = OpenPartition(key);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(ts) FROM history;";
            var result = command.ExecuteScalar();
            if (result is long value && (oldest is null || value < oldest.Value))
            {
                oldest = value;
            }
        }

        _oldestCommittedTicks = oldest;
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
        => Task.FromResult(Query(query));

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
        => new(GetSampleAtOrBefore(propertyPath, asOf));

    public HistorySeries Query(HistoryQuery query)
    {
        // A single SqliteConnection/SqliteCommand is not thread-safe, and the TWA path mutates
        // connection-global state via ATTACH/DETACH. Serialize all connection use under the
        // re-entrant _connectionLock so concurrent queries and the flush loop cannot collide on a
        // shared cached connection. The reader runs entirely within this lock (it opens connections
        // through the engine's OpenPartition/OpenMoves delegates, which re-enter this lock).
        lock (_connectionLock)
        {
            // The bucket reader's TWA carry-seed look-back reads directly through the same context; it runs
            // while this lock is already held, matching the original inline GetSampleAtOrBefore look-back.
            return query.Bucket is null
                ? SqliteHistoryReader.QueryRaw(_readContext, query)
                : SqliteBucketReader.QueryBucketed(
                    _readContext, query,
                    (path, asOf) => SqliteHistoryReader.GetSampleAtOrBefore(_readContext, path, asOf));
        }
    }

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        // Serialize connection use under the re-entrant _connectionLock (see Query for the rationale).
        lock (_connectionLock)
        {
            return SqliteHistoryReader.GetSampleAtOrBefore(_readContext, propertyPath, asOf);
        }
    }

    public void Sweep()
    {
        var cutoff = _getUtcNow() - _maxAge;

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

            // RefreshOldestCommitted also touches connections, so keep it inside the lock.
            RefreshOldestCommitted();
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

        // The native pool can keep a file handle open even after Close/Dispose on Windows.
        SqliteConnection.ClearAllPools();

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

            var connection = new SqliteConnection($"Data Source={_readContext.PartitionFilePath(key)}");
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

    // Opens (and caches) the moves database under the fixed MovesKey, creating the moves table on first
    // open. Reuses the partition connection cache so Dispose closes it too; MovesKey is filtered out of
    // partition enumeration by SqlitePartition.IsPartitionKey, so Sweep never touches it.
    private SqliteConnection OpenMoves()
    {
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(MovesKey, out var existing))
            {
                return existing;
            }

            var connection = new SqliteConnection($"Data Source={_readContext.PartitionFilePath(MovesKey)}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection,
                "CREATE TABLE IF NOT EXISTS moves (ts INTEGER NOT NULL, from_path TEXT NOT NULL, to_path TEXT NOT NULL);");
            _connections[MovesKey] = connection;
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
            foreach (var connection in _connections.Values)
            {
                connection.Close();
                connection.Dispose();
            }

            _connections.Clear();
        }

        // Release the native connection pool so the test fixture can delete the WAL/SHM files.
        SqliteConnection.ClearAllPools();
    }
}
