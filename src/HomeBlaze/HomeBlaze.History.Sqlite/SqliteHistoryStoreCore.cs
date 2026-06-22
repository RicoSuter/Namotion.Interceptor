using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// The graph-free, SQL-backed history engine. Operates only on canonical path strings and typed
/// values: partition-file management, schema, batched write plus periodic flush, raw queries,
/// look-back, coverage, and metrics. Mirrors the value routing and point mapping of
/// <c>InMemoryHistoryStoreCore</c> so query results are identical, but persists rows into
/// partitioned SQLite database files with <c>value_json</c> stored as TEXT.
/// </summary>
internal sealed class SqliteHistoryStoreCore : IDisposable
{
    private readonly string _databaseDirectory;
    private readonly PartitionInterval _partitionInterval;
    private readonly TimeSpan _maxAge;
    private readonly int _maxJsonSize;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly DateTimeOffset _startTime;

    private readonly object _pendingLock = new();
    private readonly List<PendingSample> _pending = new();

    private readonly object _connectionLock = new();
    private readonly Dictionary<string, SqliteConnection> _connections = new(StringComparer.Ordinal);

    private long _recordedCount;
    private long _oversizeCount;

    private long? _lastCommittedTicks;
    private long? _oldestCommittedTicks;
    private DateTimeOffset? _lastFlushUtc;
    private string? _lastError;

    public SqliteHistoryStoreCore(
        string databaseDirectory,
        PartitionInterval partitionInterval,
        TimeSpan maxAge,
        int maxJsonSize,
        Func<DateTimeOffset> getUtcNow)
    {
        _databaseDirectory = databaseDirectory;
        _partitionInterval = partitionInterval;
        _maxAge = maxAge;
        _maxJsonSize = maxJsonSize;
        _getUtcNow = getUtcNow;
        _startTime = getUtcNow();

        Directory.CreateDirectory(_databaseDirectory);
    }

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

    public HistoryCoverage Coverage
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
        var column = HistoryColumns.ValueColumnFor(propertyType);
        var isUlong = HistoryColumns.IsUlongProperty(propertyType);
        var row = CreateRow(value, column, isUlong);

        lock (_pendingLock)
        {
            _pending.Add(new PendingSample(propertyPath, timestamp, row, column, isUlong));
        }

        Interlocked.Increment(ref _recordedCount);
    }

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
    {
        // Move-chain recording and resolution arrives in Task 5.5; the signature is here so the surface compiles.
        throw new NotImplementedException("Task 5.5");
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        PendingSample[] batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                return Task.CompletedTask;
            }

            batch = _pending.ToArray();
            _pending.Clear();
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

            var maxTicks = _lastCommittedTicks;
            foreach (var (key, samples) in byPartition)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partitionMax = WritePartition(key, samples);
                if (maxTicks is null || partitionMax > maxTicks.Value)
                {
                    maxTicks = partitionMax;
                }
            }

            _lastCommittedTicks = maxTicks;
            RefreshOldestCommitted();
            _lastFlushUtc = _getUtcNow();
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            throw;
        }

        return Task.CompletedTask;
    }

    private long WritePartition(string key, List<PendingSample> samples)
    {
        var connection = OpenPartition(key);

        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR REPLACE INTO history (ts, path, value_long, value_double, value_json) " +
            "VALUES (@ts, @path, @long, @double, @json);";
        var tsParameter = insert.Parameters.Add("@ts", SqliteType.Integer);
        var pathParameter = insert.Parameters.Add("@path", SqliteType.Text);
        var longParameter = insert.Parameters.Add("@long", SqliteType.Integer);
        var doubleParameter = insert.Parameters.Add("@double", SqliteType.Real);
        var jsonParameter = insert.Parameters.Add("@json", SqliteType.Text);

        using var meta = connection.CreateCommand();
        meta.Transaction = transaction;
        meta.CommandText =
            "INSERT OR REPLACE INTO path_meta (path, column, is_ulong) VALUES (@path, @column, @is_ulong);";
        var metaPathParameter = meta.Parameters.Add("@path", SqliteType.Text);
        var metaColumnParameter = meta.Parameters.Add("@column", SqliteType.Integer);
        var metaUlongParameter = meta.Parameters.Add("@is_ulong", SqliteType.Integer);

        var maxTicks = long.MinValue;
        foreach (var sample in samples)
        {
            var ticks = EpochTicks.ToEpochTicks(sample.Timestamp);
            tsParameter.Value = ticks;
            pathParameter.Value = sample.Path;
            longParameter.Value = (object?)sample.Row.Long ?? DBNull.Value;
            doubleParameter.Value = (object?)sample.Row.Double ?? DBNull.Value;
            jsonParameter.Value = (object?)sample.Row.Json ?? DBNull.Value;
            insert.ExecuteNonQuery();

            metaPathParameter.Value = sample.Path;
            metaColumnParameter.Value = (int)sample.Column;
            metaUlongParameter.Value = sample.IsUlong ? 1 : 0;
            meta.ExecuteNonQuery();

            if (ticks > maxTicks)
            {
                maxTicks = ticks;
            }
        }

        transaction.Commit();
        return maxTicks;
    }

    private void RefreshOldestCommitted()
    {
        long? oldest = null;
        foreach (var key in EnumeratePartitionFileKeys())
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

    public HistorySeries Query(HistoryQuery query)
    {
        return query.Bucket is null ? QueryRaw(query) : QueryBucketed(query);
    }

    private HistorySeries QueryRaw(HistoryQuery query)
    {
        var fromTicks = EpochTicks.ToEpochTicks(query.From);
        var toTicks = EpochTicks.ToEpochTicks(query.To);
        var limit = query.MaxPoints + 1; // +1 overflow probe to detect truncation

        // Single-partition read for now (multi-partition union arrives in Task 5.2). Read the
        // partition holding From and any later partitions that exist within [from, to).
        var rows = new List<RawRow>();
        foreach (var key in PartitionKeysOverlapping(query.From, query.To))
        {
            if (!PartitionFileExists(key))
            {
                continue;
            }

            var connection = OpenPartition(key);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ts, value_long, value_double, value_json FROM history " +
                "WHERE path = @path AND ts >= @from AND ts < @to ORDER BY ts DESC LIMIT @limit;";
            command.Parameters.AddWithValue("@path", query.PropertyPath);
            command.Parameters.AddWithValue("@from", fromTicks);
            command.Parameters.AddWithValue("@to", toTicks);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadRawRow(reader));
            }
        }

        var isUlong = ResolveIsUlong(query.PropertyPath);

        // Order descending across the union, take newest (MaxPoints + 1), detect truncation, return ascending.
        rows.Sort((left, right) => right.Ticks.CompareTo(left.Ticks));
        var truncated = rows.Count > query.MaxPoints;
        var kept = truncated ? rows.GetRange(0, query.MaxPoints) : rows;
        kept.Sort((left, right) => left.Ticks.CompareTo(right.Ticks));

        var points = kept.Select(row => ToPoint(row, isUlong)).ToImmutableArray();
        return new HistorySeries(query.PropertyPath, points, truncated);
    }

    private HistorySeries QueryBucketed(HistoryQuery query)
    {
        throw new NotImplementedException("Task 5.3");
    }

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        var asOfTicks = EpochTicks.ToEpochTicks(asOf);
        var isUlong = ResolveIsUlong(propertyPath);

        // Search the partition holding asOf, then earlier partitions, newest match wins.
        foreach (var key in PartitionKeysAtOrBefore(asOf))
        {
            if (!PartitionFileExists(key))
            {
                continue;
            }

            var connection = OpenPartition(key);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ts, value_long, value_double, value_json FROM history " +
                "WHERE path = @path AND ts <= @asOf ORDER BY ts DESC LIMIT 1;";
            command.Parameters.AddWithValue("@path", propertyPath);
            command.Parameters.AddWithValue("@asOf", asOfTicks);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return ToPoint(ReadRawRow(reader), isUlong);
            }
        }

        return null;
    }

    public void Sweep()
    {
        // Retention file sweep arrives in Task 5.2.
    }

    private bool ResolveIsUlong(string propertyPath)
    {
        foreach (var key in EnumeratePartitionFileKeys())
        {
            var connection = OpenPartition(key);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT is_ulong FROM path_meta WHERE path = @path;";
            command.Parameters.AddWithValue("@path", propertyPath);
            var result = command.ExecuteScalar();
            if (result is long value)
            {
                return value != 0;
            }
        }

        return false;
    }

    private static RawRow ReadRawRow(SqliteDataReader reader)
    {
        var ticks = reader.GetInt64(0);
        long? longValue = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        double? doubleValue = reader.IsDBNull(2) ? null : reader.GetDouble(2);
        string? jsonValue = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new RawRow(ticks, longValue, doubleValue, jsonValue);
    }

    // Maps a stored row to the wire HistoryPoint, mirroring InMemoryHistoryStoreCore.ToPoint:
    // value_double/value_long -> Number; value_json -> Json (parsed from text); a ulong-overflow
    // JSON number -> Number as well (COALESCE) so numeric aggregation works; all-null -> empty point.
    private static HistoryPoint ToPoint(RawRow row, bool isUlong)
    {
        var timestamp = EpochTicks.FromEpochTicks(row.Ticks);

        if (row.Double is { } doubleValue)
        {
            return new HistoryPoint(timestamp, doubleValue, null);
        }

        if (row.Long is { } longValue)
        {
            return new HistoryPoint(timestamp, longValue, null);
        }

        if (row.Json is { } jsonText)
        {
            var element = JsonDocument.Parse(jsonText).RootElement.Clone();
            double? number = isUlong && element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;
            return new HistoryPoint(timestamp, number, element);
        }

        return new HistoryPoint(timestamp, null, null); // explicit recorded null
    }

    // Routes a value into a row exactly like InMemoryHistoryStoreCore.CreateSample, but serializes
    // the JSON column to its raw text representation for storage.
    private Row CreateRow(object? value, ValueColumn column, bool isUlong)
    {
        if (value is null)
        {
            return new Row(null, null, null);
        }

        switch (column)
        {
            case ValueColumn.Double:
                return new Row(null, Convert.ToDouble(value, CultureInfo.InvariantCulture), null);

            case ValueColumn.Long:
                if (isUlong && value is ulong unsigned && unsigned > long.MaxValue)
                {
                    return new Row(null, null, JsonSerializer.Serialize(unsigned));
                }

                return new Row(Convert.ToInt64(value, CultureInfo.InvariantCulture), null, null);

            case ValueColumn.Json:
            default:
                return new Row(null, null, SerializeJson(value));
        }
    }

    private string SerializeJson(object value)
    {
        // enum -> name; decimal/string -> native JSON; oversize string -> placeholder.
        JsonElement element = value is Enum
            ? JsonSerializer.SerializeToElement(value.ToString())
            : JsonSerializer.SerializeToElement(value);

        if (element.ValueKind == JsonValueKind.String)
        {
            var size = element.GetRawText().Length; // UTF-16 length is a safe upper-bound proxy for the cap
            if (size > _maxJsonSize)
            {
                Interlocked.Increment(ref _oversizeCount);
                return JsonSerializer.Serialize(new OversizePlaceholder(true, size));
            }
        }

        return element.GetRawText();
    }

    private SqliteConnection OpenPartition(string key)
    {
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var connection = new SqliteConnection($"Data Source={PartitionFilePath(key)}");
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string PartitionFilePath(string key) => Path.Combine(_databaseDirectory, key + ".db");

    private bool PartitionFileExists(string key) => File.Exists(PartitionFilePath(key));

    // Partition keys whose file already exists on disk (used by metadata and coverage queries).
    private IEnumerable<string> EnumeratePartitionFileKeys()
    {
        if (!Directory.Exists(_databaseDirectory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(_databaseDirectory, "*.db"))
        {
            yield return Path.GetFileNameWithoutExtension(file);
        }
    }

    // Single-partition coverage for Task 5.1: the partitions overlapping a query range.
    // Task 5.2 replaces this with SqlitePartition.EnumeratePartitionKeys spanning all keys in [from, to).
    private IEnumerable<string> PartitionKeysOverlapping(DateTimeOffset from, DateTimeOffset to)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            SqlitePartition.PartitionKey(from, _partitionInterval),
            SqlitePartition.PartitionKey(to, _partitionInterval)
        };
        return keys;
    }

    private IEnumerable<string> PartitionKeysAtOrBefore(DateTimeOffset asOf)
    {
        // The partition holding asOf is enough for the single-partition raw path; earlier partitions
        // are added in Task 5.2 (look-back across files).
        yield return SqlitePartition.PartitionKey(asOf, _partitionInterval);
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

    private readonly record struct Row(long? Long, double? Double, string? Json);

    private readonly record struct RawRow(long Ticks, long? Long, double? Double, string? Json);

    private readonly record struct PendingSample(
        string Path, DateTimeOffset Timestamp, Row Row, ValueColumn Column, bool IsUlong);

    private readonly record struct OversizePlaceholder(
        [property: System.Text.Json.Serialization.JsonPropertyName("$oversize")] bool Oversize,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] int Size);
}
