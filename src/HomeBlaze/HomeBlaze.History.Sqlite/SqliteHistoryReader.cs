using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>One leg of a queried path's move chain: the path it used during [ValidFrom, ValidTo).</summary>
internal readonly record struct ChainLeg(string Path, DateTimeOffset ValidFrom, DateTimeOffset ValidTo);

/// <summary>
/// One leg's slice over a single existing partition file: the leg's path, the partition key, and the
/// intersection of the query window with the leg's validity, expressed in epoch ticks.
/// </summary>
internal readonly record struct ChainSegment(string Path, string PartitionKey, long FromTicks, long ToTicks);

/// <summary>The stored column kind and ulong flag for a path, read from <c>path_meta</c>.</summary>
internal readonly record struct ColumnMeta(ValueColumn Column, bool IsUlong);

/// <summary>
/// The connection access the read helpers need, supplied by the engine: the open-partition and
/// open-moves delegates plus the partition layout (directory, interval, moves key). The engine builds
/// this while holding its connection lock and passes it in; the delegates re-enter the engine lock when
/// they open or reuse a cached connection. This context never locks itself and never caches connections.
/// </summary>
internal readonly struct SqliteReadContext(
    string databaseDirectory,
    PartitionInterval partitionInterval,
    string movesKey,
    Func<string, SqliteConnection> openPartition,
    Func<SqliteConnection> openMoves)
{
    public string MovesKey => movesKey;

    public SqliteConnection OpenPartition(string key) => openPartition(key);

    public SqliteConnection OpenMoves() => openMoves();

    public string PartitionFilePath(string key) => Path.Combine(databaseDirectory, key + ".db");

    public bool PartitionFileExists(string key) => File.Exists(PartitionFilePath(key));

    // Partition keys whose file already exists on disk (used by metadata and coverage queries).
    public IEnumerable<string> EnumeratePartitionFileKeys()
    {
        if (!Directory.Exists(databaseDirectory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(databaseDirectory, "*.db"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (SqlitePartition.IsPartitionKey(key, partitionInterval))
            {
                yield return key; // skip non-partition files such as the moves database
            }
        }
    }

    // Every partition key whose range overlaps [from, to), in ascending time order.
    public IEnumerable<string> PartitionKeysOverlapping(DateTimeOffset from, DateTimeOffset to)
    {
        return SqlitePartition.EnumeratePartitionKeys(from, to, partitionInterval);
    }

    // Existing partition files at or before asOf, newest first (look-back across files stops at the first hit).
    public IEnumerable<string> PartitionKeysAtOrBefore(DateTimeOffset asOf)
    {
        var asOfKey = SqlitePartition.PartitionKey(asOf, partitionInterval);
        return EnumeratePartitionFileKeys()
            .Where(key => string.CompareOrdinal(key, asOfKey) <= 0)
            .OrderByDescending(key => key, StringComparer.Ordinal);
    }
}

/// <summary>
/// Pure read SQL for the SQLite history engine: raw queries, at-or-before look-back, move-chain
/// resolution, and column-metadata lookup. Bucketed aggregation lives in <see cref="SqliteBucketReader"/>,
/// which reuses this class's <see cref="ResolveChain"/> and <see cref="ResolveColumnMeta"/>. Every method
/// takes a <see cref="SqliteReadContext"/> (the engine's open-connection delegates plus partition layout)
/// and uses <see cref="SqliteValueRouting"/> for value mapping. These helpers never lock and never touch
/// the engine's connection cache; the engine calls them while holding its connection lock.
/// </summary>
internal static class SqliteHistoryReader
{
    public static HistorySeries QueryRaw(SqliteReadContext context, HistoryQuery query)
    {
        var limit = query.MaxPoints + 1; // +1 overflow probe to detect truncation

        // Route through the move chain: for each leg, read its own path over the intersection of the
        // query range with the leg's [ValidFrom, ValidTo), then merge. With no moves this is a single
        // unbounded leg, identical to the pre-move single-path read.
        var rows = new List<(RawRow Row, bool IsUlong)>();
        foreach (var leg in ResolveChain(context, query.PropertyPath))
        {
            var legFrom = query.From > leg.ValidFrom ? query.From : leg.ValidFrom;
            var legTo = query.To < leg.ValidTo ? query.To : leg.ValidTo;
            if (legFrom >= legTo)
            {
                continue;
            }

            var fromTicks = EpochTicks.ToEpochTicks(legFrom);
            var toTicks = EpochTicks.ToEpochTicks(legTo);
            var isUlong = ResolveIsUlong(context, leg.Path);

            foreach (var key in context.PartitionKeysOverlapping(legFrom, legTo))
            {
                if (!context.PartitionFileExists(key))
                {
                    continue;
                }

                var connection = context.OpenPartition(key);
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

        var points = kept.Select(entry => SqliteValueRouting.ToPoint(entry.Row, entry.IsUlong)).ToImmutableArray();
        return new HistorySeries(query.PropertyPath, points, truncated);
    }

    public static HistoryPoint? GetSampleAtOrBefore(SqliteReadContext context, string propertyPath, DateTimeOffset asOf)
    {
        // Route through the move chain: walk legs from newest to oldest and return the first held value.
        // Only legs whose validity starts at or before asOf can hold the value; the older leg of a move is
        // capped at ValidTo - 1 tick (its half-open ceiling), mirroring InMemoryHistoryStore.
        foreach (var leg in ResolveChain(context, propertyPath))
        {
            if (leg.ValidFrom > asOf)
            {
                continue;
            }

            var ceiling = asOf < leg.ValidTo ? asOf : leg.ValidTo - new TimeSpan(1);
            var found = GetLegSampleAtOrBefore(context, leg.Path, ceiling);
            if (found is { } row && EpochTicks.FromEpochTicks(row.Ticks) >= leg.ValidFrom)
            {
                return SqliteValueRouting.ToPoint(row, ResolveIsUlong(context, leg.Path));
            }
        }

        return null;
    }

    // The stored column kind and ulong flag for a (possibly moved) property: the first path along its chain
    // that has path_meta. The SQLite equivalent of InMemoryHistoryStore.ResolveBuffer (which returns the
    // first buffer in the chain), used for the numeric-on-json-non-ulong guard and ulong-overflow folding.
    public static ColumnMeta? ResolveColumnMeta(SqliteReadContext context, List<ChainLeg> chain)
    {
        foreach (var leg in chain)
        {
            if (ResolveColumnMetaForPath(context, leg.Path) is { } meta)
            {
                return meta;
            }
        }

        return null;
    }

    // Builds the path chain for a queried (current) path by walking moves backward; returns legs each
    // scoped to [ValidFrom, ValidTo). With no moves a single unbounded leg [MinValue, MaxValue), so the
    // query path is unchanged. Identical algorithm to InMemoryHistoryStore.ResolveChain, but the move
    // set is read from moves.db.
    public static List<ChainLeg> ResolveChain(SqliteReadContext context, string currentPath)
    {
        var snapshot = ReadMoves(context);

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

    // Reads every move from moves.db into memory (the move set is small). Empty when moves.db has no
    // rows or does not exist yet.
    private static List<MoveRecord> ReadMoves(SqliteReadContext context)
    {
        var result = new List<MoveRecord>();
        if (!File.Exists(context.PartitionFilePath(context.MovesKey)))
        {
            return result; // no moves recorded yet -> single unbounded leg in ResolveChain
        }

        var connection = context.OpenMoves();
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

    // Newest row at or before asOf for a single path across its partitions.
    private static RawRow? GetLegSampleAtOrBefore(SqliteReadContext context, string path, DateTimeOffset asOf)
    {
        var asOfTicks = EpochTicks.ToEpochTicks(asOf);

        // Search the partition holding asOf, then earlier partitions, newest match wins.
        foreach (var key in context.PartitionKeysAtOrBefore(asOf))
        {
            if (!context.PartitionFileExists(key))
            {
                continue;
            }

            var connection = context.OpenPartition(key);
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

    // The stored column kind and ulong flag for a single path, read from path_meta (written at flush time).
    // Returns null when the path has never been written.
    private static ColumnMeta? ResolveColumnMetaForPath(SqliteReadContext context, string propertyPath)
    {
        foreach (var key in context.EnumeratePartitionFileKeys())
        {
            var connection = context.OpenPartition(key);
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

    private static bool ResolveIsUlong(SqliteReadContext context, string propertyPath)
    {
        foreach (var key in context.EnumeratePartitionFileKeys())
        {
            var connection = context.OpenPartition(key);
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
}
