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

    // Fixed connection key for the moves database. It is never a valid partition key
    // (SqlitePartition.IsPartitionKey("moves", ...) is false), so partition enumeration and Sweep skip it.
    private const string MovesKey = "moves";

    private readonly object _pendingLock = new();
    private readonly List<PendingSample> _pending = new();
    private readonly List<MoveRecord> _pendingMoves = new();

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
        var row = CreateRow(value, column, isUlong);

        lock (_pendingLock)
        {
            _pending.Add(new PendingSample(propertyPath, timestamp, row, column, isUlong));
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
                    var partitionMax = WritePartition(key, samples);
                    if (maxTicks is null || partitionMax > maxTicks.Value)
                    {
                        maxTicks = partitionMax;
                    }
                }

                if (moveBatch.Length > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMoves(moveBatch);
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

    // Persists queued moves into moves.db (caller already holds _connectionLock).
    private void WriteMoves(IReadOnlyList<MoveRecord> moves)
    {
        var connection = OpenMoves();

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO moves (ts, from_path, to_path) VALUES (@ts, @from, @to);";
        var tsParameter = insert.Parameters.Add("@ts", SqliteType.Integer);
        var fromParameter = insert.Parameters.Add("@from", SqliteType.Text);
        var toParameter = insert.Parameters.Add("@to", SqliteType.Text);

        foreach (var move in moves)
        {
            tsParameter.Value = EpochTicks.ToEpochTicks(move.Timestamp);
            fromParameter.Value = move.FromPath;
            toParameter.Value = move.ToPath;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // Builds the path chain for a queried (current) path by walking moves backward; returns legs each
    // scoped to [ValidFrom, ValidTo). With no moves a single unbounded leg [MinValue, MaxValue), so the
    // query path is unchanged. Identical algorithm to InMemoryHistoryStoreCore.ResolveChain, but the move
    // set is read from moves.db. Caller already holds _connectionLock.
    private List<ChainLeg> ResolveChain(string currentPath)
    {
        var snapshot = ReadMoves();

        var legs = new List<ChainLeg>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = currentPath;
        var validTo = DateTimeOffset.MaxValue;

        while (visited.Add(path))
        {
            // Latest move INTO this path before validTo gives the time the subject arrived here.
            MoveRecord? arrival = null;
            foreach (var move in snapshot)
            {
                if (StringComparer.Ordinal.Equals(move.ToPath, path) && move.Timestamp <= validTo &&
                    (arrival is null || move.Timestamp > arrival.Value.Timestamp))
                {
                    arrival = move;
                }
            }

            var validFrom = arrival?.Timestamp ?? DateTimeOffset.MinValue;
            legs.Add(new ChainLeg(path, validFrom, validTo));

            if (arrival is null)
            {
                break; // reached the original path
            }

            path = arrival.Value.FromPath;
            validTo = arrival.Value.Timestamp;
        }

        return legs;
    }

    // Expands a chain into concrete (path, partitionKey, tickWindow) segments over EXISTING partition files.
    // For each leg, the query window [from, to) is intersected with the leg's [ValidFrom, ValidTo); the
    // intersection is split across the partition files it overlaps. With no moves this is the single-path,
    // multi-partition segment set used before move routing.
    private List<ChainSegment> BuildChainSegments(List<ChainLeg> chain, DateTimeOffset from, DateTimeOffset to)
    {
        var segments = new List<ChainSegment>();
        foreach (var leg in chain)
        {
            var legFrom = from > leg.ValidFrom ? from : leg.ValidFrom;
            var legTo = to < leg.ValidTo ? to : leg.ValidTo;
            if (legFrom >= legTo)
            {
                continue;
            }

            var fromTicks = EpochTicks.ToEpochTicks(legFrom);
            var toTicks = EpochTicks.ToEpochTicks(legTo);
            foreach (var key in PartitionKeysOverlapping(legFrom, legTo))
            {
                if (PartitionFileExists(key))
                {
                    segments.Add(new ChainSegment(leg.Path, key, fromTicks, toTicks));
                }
            }
        }

        return segments;
    }

    // Reads every move from moves.db into memory (the move set is small). Empty when moves.db has no
    // rows or does not exist yet. Caller already holds _connectionLock.
    private List<MoveRecord> ReadMoves()
    {
        var result = new List<MoveRecord>();
        if (!File.Exists(PartitionFilePath(MovesKey)))
        {
            return result; // no moves recorded yet -> single unbounded leg in ResolveChain
        }

        var connection = OpenMoves();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ts, from_path, to_path FROM moves;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MoveRecord(
                EpochTicks.FromEpochTicks(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
        }

        return result;
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
        // A single SqliteConnection/SqliteCommand is not thread-safe, and the TWA path mutates
        // connection-global state via ATTACH/DETACH. Serialize all connection use under the
        // re-entrant _connectionLock so concurrent queries and the flush loop cannot collide on a
        // shared cached connection. OpenPartition re-enters this lock, which is safe.
        lock (_connectionLock)
        {
            return query.Bucket is null ? QueryRaw(query) : QueryBucketed(query);
        }
    }

    private HistorySeries QueryRaw(HistoryQuery query)
    {
        var limit = query.MaxPoints + 1; // +1 overflow probe to detect truncation

        // Route through the move chain: for each leg, read its own path over the intersection of the
        // query range with the leg's [ValidFrom, ValidTo), then merge. With no moves this is a single
        // unbounded leg, identical to the pre-move single-path read.
        var rows = new List<(RawRow Row, bool IsUlong)>();
        foreach (var leg in ResolveChain(query.PropertyPath))
        {
            var legFrom = query.From > leg.ValidFrom ? query.From : leg.ValidFrom;
            var legTo = query.To < leg.ValidTo ? query.To : leg.ValidTo;
            if (legFrom >= legTo)
            {
                continue;
            }

            var fromTicks = EpochTicks.ToEpochTicks(legFrom);
            var toTicks = EpochTicks.ToEpochTicks(legTo);
            var isUlong = ResolveIsUlong(leg.Path);

            foreach (var key in PartitionKeysOverlapping(legFrom, legTo))
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
                command.Parameters.AddWithValue("@path", leg.Path);
                command.Parameters.AddWithValue("@from", fromTicks);
                command.Parameters.AddWithValue("@to", toTicks);
                command.Parameters.AddWithValue("@limit", limit);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((ReadRawRow(reader), isUlong));
                }
            }
        }

        // Order descending across the union, take newest (MaxPoints + 1), detect truncation, return ascending.
        rows.Sort((left, right) => right.Row.Ticks.CompareTo(left.Row.Ticks));
        var truncated = rows.Count > query.MaxPoints;
        var kept = truncated ? rows.GetRange(0, query.MaxPoints) : rows;
        kept.Sort((left, right) => left.Row.Ticks.CompareTo(right.Row.Ticks));

        var points = kept.Select(entry => ToPoint(entry.Row, entry.IsUlong)).ToImmutableArray();
        return new HistorySeries(query.PropertyPath, points, truncated);
    }

    private HistorySeries QueryBucketed(HistoryQuery query)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;
        var bucketTicks = bucket.Ticks;

        // Resolve the move chain once: each leg owns its [ValidFrom, ValidTo) slice of time and stores its
        // samples under its own path. With no moves this is a single unbounded leg under query.PropertyPath.
        var chain = ResolveChain(query.PropertyPath);

        // Resolve the stored column kind and ulong flag from path_meta along the chain (the SQLite
        // equivalent of the InMemory buffer's Column/IsUlong, which uses the first buffer in the chain).
        // A numeric aggregation on a json-stored, non-ulong property (decimal/string/enum) is not
        // supported, mirroring InMemoryHistoryStoreCore.QueryBucketed.
        var meta = ResolveColumnMeta(chain);
        var isUlong = meta?.IsUlong ?? false;

        if (meta is { Column: ValueColumn.Json } && !isUlong && IsNumericAggregation(aggregation))
        {
            throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                });
        }

        // The aligned bucket range, intersected per leg below. A bucket straddling a move boundary draws its
        // samples from whichever leg owns each instant, exactly like InMemory's RangeAcrossChain.
        var alignedFrom = BucketAlignment.BucketStart(query.From, bucket);

        // Expand the chain into concrete (path, tickWindow) segments over existing partition files.
        var segments = BuildChainSegments(chain, alignedFrom, query.To);

        Dictionary<long, BucketPartial> partials;
        if (aggregation == HistoryAggregations.TimeWeightedAverage)
        {
            // TWA needs LEAD to see across partition files AND across legs: a sample is held until the NEXT
            // sample, which may live in a later partition file or a later leg's path. So read the numeric
            // samples over the UNION ALL of all segments in one ordered query (others ATTACHed), mirroring
            // InMemory which merges every leg's samples into one ascending list before integrating.
            partials = ReadTimeWeightedAveragePartials(segments, isUlong, bucketTicks);
        }
        else
        {
            partials = new Dictionary<long, BucketPartial>();
            foreach (var segment in segments)
            {
                var connection = OpenPartition(segment.PartitionKey);
                foreach (var partial in ReadPartials(connection, segment.Path, aggregation, isUlong,
                             bucketTicks, segment.FromTicks, segment.ToTicks))
                {
                    partials[partial.BucketStartTicks] = partials.TryGetValue(partial.BucketStartTicks, out var existing)
                        ? BucketPartial.Combine(existing, partial)
                        : partial;
                }
            }
        }

        // Seed the carry for Last from the merger-supplied CarrySeed.
        var carrySeedNumber = aggregation == HistoryAggregations.Last ? query.CarrySeed?.Number : null;
        var carrySeedJson = aggregation == HistoryAggregations.Last ? query.CarrySeed?.Json : null;

        // For TWA, seed the held value entering the range from the merger-supplied CarrySeed, else this
        // store's own at-or-before look-back at BucketStart(From), so an empty leading bucket is not a gap.
        // Mirrors InMemoryHistoryStoreCore.QueryBucketed.
        if (aggregation == HistoryAggregations.TimeWeightedAverage)
        {
            carrySeedNumber = query.CarrySeed?.Number
                ?? GetSampleAtOrBefore(query.PropertyPath, alignedFrom)?.Number;
        }

        return BucketAssembler.Assemble(query, partials, carrySeedNumber, carrySeedJson);
    }

    private static bool IsNumericAggregation(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    // One grouped query per partition producing the partials for the requested aggregation. Only the
    // columns the aggregation needs are fetched. The bucket key is (ts/@b)*@b on epoch ticks, which equals
    // BucketAlignment.BucketStart for the same bucket size.
    private IEnumerable<BucketPartial> ReadPartials(
        SqliteConnection connection, string propertyPath, string aggregation, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        if (aggregation is HistoryAggregations.First or HistoryAggregations.Last)
        {
            return ReadEdgePartials(connection, propertyPath, aggregation, isUlong, bucketTicks, fromTicks, toTicks);
        }

        if (aggregation == HistoryAggregations.Count)
        {
            return ReadCountPartials(connection, propertyPath, bucketTicks, fromTicks, toTicks);
        }

        return ReadNumericPartials(connection, propertyPath, isUlong, bucketTicks, fromTicks, toTicks);
    }

    // Time-weighted average: per bucket, the IN-BUCKET integral only. Each numeric sample's value is held
    // over [ts, min(nextNumericTs, bucketEnd)); LEAD bridges across non-numeric/null rows because those are
    // filtered out of the ordered set (mirroring InMemory, where a null sample leaves the held value unchanged).
    // The leading interval [bucketStart, firstNumericTs) and empty-bucket carry are supplied by BucketAssembler,
    // which also needs FirstTicks (the leading-interval boundary) and LastNumber (the carry-advance value).
    //
    // Unlike the other aggregations, TWA cannot be computed per segment then summed: a sample is held until the
    // NEXT sample, which may live in a LATER partition file OR a LATER move-chain leg's path, so LEAD must see
    // the union of all segments in one ordered scan (mirroring InMemory, which merges every leg's samples into
    // one ascending list before integrating). Each distinct partition file is ATTACHed once; one UNION term per
    // segment selects that segment's path over its tick window from the file's alias.
    //
    // v is cast to REAL so v * duration is floating point: tick products are huge (value ~tens times ~10^8
    // ticks per 10s) and an integer SUM could overflow; the weighted_sum/total_duration RATIO is unit-free.
    private Dictionary<long, BucketPartial> ReadTimeWeightedAveragePartials(
        IReadOnlyList<ChainSegment> segments, bool isUlong, long bucketTicks)
    {
        var result = new Dictionary<long, BucketPartial>();
        if (segments.Count == 0)
        {
            return result;
        }

        var numeric = isUlong
            ? "CAST(COALESCE(value_double, value_long, CAST(value_json AS REAL)) AS REAL)"
            : "CAST(COALESCE(value_double, value_long) AS REAL)";

        // Open one connection on the first segment's file as "main"; ATTACH every other distinct file once
        // under a stable alias so each segment can reference its file by alias.
        var connection = OpenPartition(segments[0].PartitionKey);
        var aliasByKey = new Dictionary<string, string>(StringComparer.Ordinal) { [segments[0].PartitionKey] = "main" };
        var aliases = new List<string>();

        var orderedTerms = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Parameters.AddWithValue("@b", bucketTicks);
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                if (!aliasByKey.TryGetValue(segment.PartitionKey, out var alias))
                {
                    alias = "p" + aliases.Count.ToString(CultureInfo.InvariantCulture);
                    aliases.Add(alias);
                    aliasByKey[segment.PartitionKey] = alias;

                    using var attach = connection.CreateCommand();
                    attach.CommandText = "ATTACH DATABASE @file AS " + alias + ";";
                    attach.Parameters.AddWithValue("@file", PartitionFilePath(segment.PartitionKey));
                    attach.ExecuteNonQuery();
                }

                var pathParameter = "@path" + index.ToString(CultureInfo.InvariantCulture);
                var fromParameter = "@from" + index.ToString(CultureInfo.InvariantCulture);
                var toParameter = "@to" + index.ToString(CultureInfo.InvariantCulture);
                orderedTerms.Add(
                    "SELECT ts, " + numeric + " AS v FROM " + alias + ".history " +
                    "WHERE path = " + pathParameter + " AND ts >= " + fromParameter + " AND ts < " + toParameter +
                    " AND " + numeric + " IS NOT NULL");
                command.Parameters.AddWithValue(pathParameter, segment.Path);
                command.Parameters.AddWithValue(fromParameter, segment.FromTicks);
                command.Parameters.AddWithValue(toParameter, segment.ToTicks);
            }

            try
            {
                command.CommandText =
                    "WITH ordered AS (" + string.Join(" UNION ALL ", orderedTerms) + "), " +
                    "edged AS (" +
                    "  SELECT ts, v, (ts/@b)*@b AS bucket_start, ((ts/@b)*@b) + @b AS bucket_end, " +
                    "         COALESCE(LEAD(ts) OVER (ORDER BY ts), ((ts/@b)*@b) + @b) AS next_ts, " +
                    "         LAST_VALUE(v) OVER (PARTITION BY (ts/@b)*@b ORDER BY ts " +
                    "             ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS last_v " +
                    "  FROM ordered" +
                    ") " +
                    "SELECT bucket_start, " +
                    "       MIN(ts) AS first_ts, " +
                    "       SUM(v * (CASE WHEN next_ts < bucket_end THEN next_ts ELSE bucket_end END - ts)) AS weighted_sum, " +
                    "       SUM(CASE WHEN next_ts < bucket_end THEN next_ts ELSE bucket_end END - ts) AS total_duration, " +
                    "       MAX(last_v) AS last_number " + // last_v is constant within a bucket, so MAX returns it
                    "FROM edged GROUP BY bucket_start ORDER BY bucket_start;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var bucketStart = reader.GetInt64(0);
                    long? firstTicks = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                    var weightedSum = reader.IsDBNull(2) ? 0d : reader.GetDouble(2);
                    var totalDuration = reader.IsDBNull(3) ? 0d : reader.GetDouble(3);
                    double? lastNumber = reader.IsDBNull(4) ? null : reader.GetDouble(4);

                    result[bucketStart] = new BucketPartial(
                        bucketStart, 0, null, null, null, null,
                        firstTicks, null, null, null, lastNumber, null, weightedSum, totalDuration);
                }
            }
            finally
            {
                foreach (var alias in aliases)
                {
                    using var detach = connection.CreateCommand();
                    detach.CommandText = "DETACH DATABASE " + alias + ";";
                    detach.ExecuteNonQuery();
                }
            }
        }

        return result;
    }

    // First/Last: the earliest (MIN ts) or latest (MAX ts) row per bucket, with its raw value columns.
    private List<BucketPartial> ReadEdgePartials(
        SqliteConnection connection, string propertyPath, string aggregation, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        var isFirst = aggregation == HistoryAggregations.First;
        var edge = isFirst ? "MIN(ts)" : "MAX(ts)";

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (h.ts/@b)*@b AS bucket, h.ts, h.value_long, h.value_double, h.value_json FROM history h " +
            "WHERE h.path = @path AND h.ts >= @from AND h.ts < @to " +
            "AND h.ts = (SELECT " + edge + " FROM history WHERE path = @path AND (ts/@b)*@b = (h.ts/@b)*@b " +
            "AND ts >= @from AND ts < @to) " +
            "GROUP BY bucket;";
        command.Parameters.AddWithValue("@path", propertyPath);
        command.Parameters.AddWithValue("@b", bucketTicks);
        command.Parameters.AddWithValue("@from", fromTicks);
        command.Parameters.AddWithValue("@to", toTicks);

        var result = new List<BucketPartial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bucketStart = reader.GetInt64(0);
            var ts = reader.GetInt64(1);
            long? longValue = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            double? doubleValue = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            string? jsonValue = reader.IsDBNull(4) ? null : reader.GetString(4);

            // The numeric projection for an edge sample mirrors ToPoint: double/long, plus a ulong-overflow
            // JSON number folded in via path_meta.is_ulong (the rare case where a ulong exceeds long.MaxValue).
            double? number = doubleValue ?? (double?)longValue;
            if (number is null && isUlong && jsonValue is not null)
            {
                using var document = JsonDocument.Parse(jsonValue);
                if (document.RootElement.ValueKind == JsonValueKind.Number)
                {
                    number = document.RootElement.GetDouble();
                }
            }

            if (isFirst)
            {
                result.Add(new BucketPartial(
                    bucketStart, 0, null, null, null, null,
                    ts, number, jsonValue, null, null, null, 0, 0));
            }
            else
            {
                result.Add(new BucketPartial(
                    bucketStart, 0, null, null, null, null,
                    null, null, null, ts, number, jsonValue, 0, 0));
            }
        }

        return result;
    }

    // Count: total number of samples per bucket (COUNT(*)), matching InMemory's samples.Count, which
    // includes non-numeric and explicit-null samples (Count is allowed on any column type).
    private List<BucketPartial> ReadCountPartials(
        SqliteConnection connection, string propertyPath, long bucketTicks, long fromTicks, long toTicks)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (ts/@b)*@b AS bucket, COUNT(*) AS cnt FROM history " +
            "WHERE path = @path AND ts >= @from AND ts < @to GROUP BY bucket ORDER BY bucket;";
        command.Parameters.AddWithValue("@path", propertyPath);
        command.Parameters.AddWithValue("@b", bucketTicks);
        command.Parameters.AddWithValue("@from", fromTicks);
        command.Parameters.AddWithValue("@to", toTicks);

        var result = new List<BucketPartial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BucketPartial(
                reader.GetInt64(0), reader.GetInt64(1), null, null, null, null,
                null, null, null, null, null, null, 0, 0));
        }

        return result;
    }

    // Sum/Min/Max/SampleAverage/StandardDeviation: grouped numeric reductions over COALESCE(value_double, value_long).
    // When the property is ulong, value_json numbers (ulong overflow) also count as numeric values; SQLite's
    // COALESCE includes value_json (numeric text parses to a number) so the reductions fold it in too.
    private List<BucketPartial> ReadNumericPartials(
        SqliteConnection connection, string propertyPath, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        // The numeric expression: for ulong properties also fold value_json (a JSON number stored as text).
        var numeric = isUlong
            ? "COALESCE(value_double, value_long, CAST(value_json AS REAL))"
            : "COALESCE(value_double, value_long)";

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (ts/@b)*@b AS bucket, " +
            "COUNT(" + numeric + ") AS cnt, " +
            "SUM(" + numeric + ") AS sum_num, " +
            "MIN(" + numeric + ") AS min_num, " +
            "MAX(" + numeric + ") AS max_num, " +
            "SUM(" + numeric + " * " + numeric + ") AS sumsq_num " +
            "FROM history WHERE path = @path AND ts >= @from AND ts < @to " +
            "GROUP BY bucket ORDER BY bucket;";
        command.Parameters.AddWithValue("@path", propertyPath);
        command.Parameters.AddWithValue("@b", bucketTicks);
        command.Parameters.AddWithValue("@from", fromTicks);
        command.Parameters.AddWithValue("@to", toTicks);

        var result = new List<BucketPartial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bucketStart = reader.GetInt64(0);
            var count = reader.GetInt64(1); // COUNT(numeric) = number of non-null numeric values
            double? sum = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            double? min = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            double? max = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            double? sumSquares = reader.IsDBNull(5) ? null : reader.GetDouble(5);

            result.Add(new BucketPartial(
                bucketStart, count, sum, min, max, sumSquares,
                null, null, null, null, null, null, 0, 0));
        }

        return result;
    }

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        // Serialize connection use under the re-entrant _connectionLock (see Query for the rationale).
        lock (_connectionLock)
        {
            // Route through the move chain: walk legs from newest to oldest and return the first held value.
            // Only legs whose validity starts at or before asOf can hold the value; the older leg of a move is
            // capped at ValidTo - 1 tick (its half-open ceiling), mirroring InMemoryHistoryStoreCore.
            foreach (var leg in ResolveChain(propertyPath))
            {
                if (leg.ValidFrom > asOf)
                {
                    continue;
                }

                var ceiling = asOf < leg.ValidTo ? asOf : leg.ValidTo - new TimeSpan(1);
                var found = GetLegSampleAtOrBefore(leg.Path, ceiling);
                if (found is { } row && EpochTicks.FromEpochTicks(row.Ticks) >= leg.ValidFrom)
                {
                    return ToPoint(row, ResolveIsUlong(leg.Path));
                }
            }

            return null;
        }
    }

    // Newest row at or before asOf for a single path across its partitions. Caller already holds _connectionLock.
    private RawRow? GetLegSampleAtOrBefore(string path, DateTimeOffset asOf)
    {
        var asOfTicks = EpochTicks.ToEpochTicks(asOf);

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
            command.Parameters.AddWithValue("@path", path);
            command.Parameters.AddWithValue("@asOf", asOfTicks);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return ReadRawRow(reader);
            }
        }

        return null;
    }

    public void Sweep()
    {
        var cutoff = _getUtcNow() - _maxAge;

        lock (_connectionLock)
        {
            foreach (var key in EnumeratePartitionFileKeys().ToArray())
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

        var path = PartitionFilePath(key);
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

    // The stored column kind and ulong flag for a (possibly moved) property: the first path along its chain
    // that has path_meta. The SQLite equivalent of InMemoryHistoryStoreCore.ResolveBuffer (which returns the
    // first buffer in the chain), used for the numeric-on-json-non-ulong guard and ulong-overflow folding.
    private ColumnMeta? ResolveColumnMeta(List<ChainLeg> chain)
    {
        foreach (var leg in chain)
        {
            if (ResolveColumnMetaForPath(leg.Path) is { } meta)
            {
                return meta;
            }
        }

        return null;
    }

    // The stored column kind and ulong flag for a single path, read from path_meta (written at flush time).
    // Returns null when the path has never been written.
    private ColumnMeta? ResolveColumnMetaForPath(string propertyPath)
    {
        foreach (var key in EnumeratePartitionFileKeys())
        {
            var connection = OpenPartition(key);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT column, is_ulong FROM path_meta WHERE path = @path;";
            command.Parameters.AddWithValue("@path", propertyPath);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new ColumnMeta((ValueColumn)reader.GetInt64(0), reader.GetInt64(1) != 0);
            }
        }

        return null;
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

            var connection = new SqliteConnection($"Data Source={PartitionFilePath(MovesKey)}");
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
            var key = Path.GetFileNameWithoutExtension(file);
            if (SqlitePartition.IsPartitionKey(key, _partitionInterval))
            {
                yield return key; // skip non-partition files such as the moves database
            }
        }
    }

    // Every partition key whose range overlaps [from, to), in ascending time order.
    private IEnumerable<string> PartitionKeysOverlapping(DateTimeOffset from, DateTimeOffset to)
    {
        return SqlitePartition.EnumeratePartitionKeys(from, to, _partitionInterval);
    }

    // Existing partition files at or before asOf, newest first (look-back across files stops at the first hit).
    private IEnumerable<string> PartitionKeysAtOrBefore(DateTimeOffset asOf)
    {
        var asOfKey = SqlitePartition.PartitionKey(asOf, _partitionInterval);
        return EnumeratePartitionFileKeys()
            .Where(key => string.CompareOrdinal(key, asOfKey) <= 0)
            .OrderByDescending(key => key, StringComparer.Ordinal);
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

    private readonly record struct ColumnMeta(ValueColumn Column, bool IsUlong);

    private readonly record struct MoveRecord(DateTimeOffset Timestamp, string FromPath, string ToPath);

    private readonly record struct ChainLeg(string Path, DateTimeOffset ValidFrom, DateTimeOffset ValidTo);

    // One leg's slice over a single existing partition file: the leg's path, the partition key, and the
    // intersection of the query window with the leg's validity, expressed in epoch ticks.
    private readonly record struct ChainSegment(string Path, string PartitionKey, long FromTicks, long ToTicks);

    private readonly record struct Row(long? Long, double? Double, string? Json);

    private readonly record struct RawRow(long Ticks, long? Long, double? Double, string? Json);

    private readonly record struct PendingSample(
        string Path, DateTimeOffset Timestamp, Row Row, ValueColumn Column, bool IsUlong);

    private readonly record struct OversizePlaceholder(
        [property: System.Text.Json.Serialization.JsonPropertyName("$oversize")] bool Oversize,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] int Size);
}
