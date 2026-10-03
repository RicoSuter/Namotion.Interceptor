using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Pure bucketed-aggregation read SQL for the SQLite history engine: the bucketed query orchestration
/// plus the four partial readers (first/last edge, count, numeric reductions, and the
/// time-weighted-average ordered event scan). It reuses <see cref="SqliteHistoryReader"/> for move-chain
/// resolution and column metadata, <see cref="SqliteValueRouting"/> for value mapping, and
/// <see cref="BucketAssembler"/> for final assembly. Every method takes a <see cref="SqliteReadContext"/>
/// (the engine's open-connection delegates plus partition layout); these helpers never lock and never
/// touch the engine's connection cache. The engine calls them while holding its connection lock.
/// </summary>
internal static class SqliteBucketReader
{
    // The time-weighted-average scan. The value is read as REAL so value * duration is floating point: tick
    // products are huge (value ~tens times ~10^8 ticks per 10s) and an integer sum could overflow; the
    // weightedSum/totalDuration ratio is unit-free.
    private const string TimeWeightedAverageSql =
        "SELECT ts, CAST(COALESCE(value_double, value_long) AS REAL) AS v FROM history " +
        "WHERE path_id = @path_id AND ts >= @from AND ts < @to ORDER BY ts;";

    // As TimeWeightedAverageSql, also folding a ulong overflow stored as a JSON number.
    private const string UlongTimeWeightedAverageSql =
        "SELECT ts, CAST(COALESCE(value_double, value_long, CAST(value_json AS REAL)) AS REAL) AS v FROM history " +
        "WHERE path_id = @path_id AND ts >= @from AND ts < @to ORDER BY ts;";

    public static HistorySeries QueryBucketed(
        SqliteReadContext context,
        HistoryQuery query,
        Func<string, DateTimeOffset, HistoryPoint?> getSampleAtOrBefore,
        ImmutableArray<HistoryCoverage> coverageRanges,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;
        var bucketTicks = bucket.Ticks;

        // Resolve the move chain once: each leg owns its [ValidFrom, ValidTo) slice of time and stores its
        // samples under its own path. With no moves this is a single unbounded leg under query.PropertyPath.
        var chain = SqliteHistoryReader.ResolveChain(context, query.PropertyPath);

        // Resolve the stored column kind and ulong flag from the paths table along the chain (the SQLite
        // equivalent of the InMemory buffer's Column/IsUlong, which uses the first buffer in the chain).
        // A numeric aggregation on a json-stored, non-ulong property (string/enum) is not
        // supported, mirroring InMemoryHistoryStore.QueryBucketed.
        var meta = SqliteHistoryReader.ResolveColumnMeta(context, chain);
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
        var alignedFrom = BucketAlignment.FirstBucketStart(
            query.From, query.To, bucket, query.MaxPoints);

        var isCarryDependent = aggregation is
            HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;
        var carrySeed = isCarryDependent ? query.CarrySeed : null;
        var originalAlignedFrom = BucketAlignment.BucketStart(query.From, bucket);

        // When MaxPoints clipped older buckets, the look-back at the clipped boundary replaces the seed even
        // when nothing is held there, since the seed may not survive a coverage gap in between. Otherwise
        // this store's own held value stands in when the merger supplied no seed.
        if (isCarryDependent &&
            (alignedFrom > originalAlignedFrom || query.CarrySeed is null))
        {
            carrySeed = getSampleAtOrBefore(query.PropertyPath, alignedFrom);
        }

        // Reads are limited to what this store covers, so samples outside coverage never reach a bucket.
        var windows = HistoryCoverage.Clip(coverageRanges, new HistoryCoverage(alignedFrom, query.To));
        var partials = new Dictionary<long, BucketPartial>();
        if (windows.Length > 0)
        {
            // Partition files and path ids are resolved once per query, not per window: there is a coverage
            // window per restart, so per-window lookups multiplied the directory scans by the restart count.
            var partitions = new List<(string Key, DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var partition in context.PartitionRangesOverlapping(windows[0].From, windows[^1].To))
            {
                if (context.PartitionFileExists(partition.Key))
                {
                    partitions.Add(partition);
                }
            }

            var pathIds = new Dictionary<(string PartitionKey, string Path), long?>();
            var segments = new List<ChainSegment>();
            var timeWeightedAverageSql = isUlong ? UlongTimeWeightedAverageSql : TimeWeightedAverageSql;
            var firstPartition = 0;
            for (var windowIndex = 0; windowIndex < windows.Length; windowIndex++)
            {
                var window = windows[windowIndex];
                firstPartition = CollectWindowSegments(chain, partitions, firstPartition, window, segments);
                if (aggregation == HistoryAggregations.TimeWeightedAverage)
                {
                    // The held value only enters the scan when this store covers the first bucket's start.
                    var seed = windowIndex == 0 && window.From == alignedFrom ? carrySeed?.Number : null;
                    ReadTimeWeightedAverageWindow(
                        context, pathIds, segments, window, timeWeightedAverageSql, bucketTicks, seed, partials,
                        cancellationToken);
                    continue;
                }

                foreach (var segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var connection = context.OpenPartition(segment.PartitionKey);
                    if (connection is null || ResolvePathId(pathIds, connection, segment) is not { } pathId)
                    {
                        continue; // unreadable partition, or one that never saw the path
                    }

                    foreach (var partial in ReadPartials(connection, pathId, aggregation, isUlong,
                                 bucketTicks, segment.FromTicks, segment.ToTicks))
                    {
                        partials[partial.BucketStartTicks] = partials.TryGetValue(partial.BucketStartTicks, out var existing)
                            ? BucketPartial.Combine(existing, partial)
                            : partial;
                    }
                }
            }
        }

        return BucketAssembler.Assemble(
            query, partials, aggregation == HistoryAggregations.Last ? carrySeed : null, windows);
    }

    // Replaces segments with the window's (path, partitionKey, tickWindow) slices: per leg, the window
    // intersected with the leg's [ValidFrom, ValidTo), split across the partitions it overlaps, legs in chain
    // order and partitions in the given ascending-start order. Windows must be passed ascending; the return
    // value is the first partition a later window can still overlap, to pass back in as firstPartition.
    private static int CollectWindowSegments(
        List<HistoryChainLeg> chain,
        List<(string Key, DateTimeOffset Start, DateTimeOffset End)> partitions,
        int firstPartition,
        HistoryCoverage window,
        List<ChainSegment> segments)
    {
        segments.Clear();
        while (firstPartition < partitions.Count && partitions[firstPartition].End <= window.From)
        {
            firstPartition++;
        }

        foreach (var leg in chain)
        {
            var legFrom = window.From > leg.ValidFrom ? window.From : leg.ValidFrom;
            var legTo = window.To < leg.ValidTo ? window.To : leg.ValidTo;
            if (legFrom >= legTo)
            {
                continue;
            }

            var fromTicks = EpochTicks.ToEpochTicks(legFrom);
            var toTicks = EpochTicks.ToEpochTicks(legTo);
            for (var index = firstPartition; index < partitions.Count && partitions[index].Start < legTo; index++)
            {
                if (partitions[index].End > legFrom)
                {
                    segments.Add(new ChainSegment(leg.Path, partitions[index].Key, fromTicks, toTicks));
                }
            }
        }

        return firstPartition;
    }

    // SqliteHistoryReader.ResolvePathId, memoized per (partition, path) for the query.
    private static long? ResolvePathId(
        Dictionary<(string PartitionKey, string Path), long?> pathIds, SqliteConnection connection, ChainSegment segment)
    {
        ref var pathId = ref CollectionsMarshal.GetValueRefOrAddDefault(
            pathIds, (segment.PartitionKey, segment.Path), out var exists);
        if (!exists)
        {
            pathId = SqliteHistoryReader.ResolvePathId(connection, segment.Path);
        }

        return pathId;
    }

    private static bool IsNumericAggregation(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    // One grouped query per partition producing the partials for the requested aggregation. Only the
    // columns the aggregation needs are fetched. The bucket key is (ts/@b)*@b on epoch ticks, which equals
    // BucketAlignment.BucketStart for the same bucket size.
    private static IEnumerable<BucketPartial> ReadPartials(
        SqliteConnection connection, long pathId, string aggregation, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        if (aggregation is HistoryAggregations.First or HistoryAggregations.Last)
        {
            return ReadEdgePartials(connection, pathId, aggregation, isUlong, bucketTicks, fromTicks, toTicks);
        }

        if (aggregation == HistoryAggregations.Count)
        {
            return ReadCountPartials(connection, pathId, bucketTicks, fromTicks, toTicks);
        }

        return ReadNumericPartials(connection, pathId, isUlong, bucketTicks, fromTicks, toTicks);
    }

    // Time-weighted average for one coverage window: integrates value * duration per bucket over covered
    // time only, so the assembler just divides. A window after a gap starts with nothing held, and the
    // window's last value holds to the window end, which also integrates a quiet covered stretch that has
    // no partition file. Explicit null events stay in the ordered set: they end the held value, and the
    // interval up to the next numeric event adds nothing.
    //
    // Unlike the other aggregations, TWA must see one ascending event stream across partition files and
    // move legs. The segments of a window are disjoint time slices, so ordering them and streaming each
    // SQL reader in timestamp order reconstructs that stream with constant sample memory and no SQLite
    // ATTACH limit. The segments list is sorted in place.
    private static void ReadTimeWeightedAverageWindow(
        SqliteReadContext context,
        Dictionary<(string PartitionKey, string Path), long?> pathIds,
        List<ChainSegment> segments,
        HistoryCoverage window,
        string sql,
        long bucketTicks,
        double? seed,
        Dictionary<long, BucketPartial> result,
        CancellationToken cancellationToken)
    {
        var previousTicks = EpochTicks.ToEpochTicks(window.From);
        var previousValue = seed;

        segments.Sort(CompareByTime);
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = context.OpenPartition(segment.PartitionKey);
            if (connection is null || ResolvePathId(pathIds, connection, segment) is not { } pathId)
            {
                continue; // unreadable partition, or one that never saw the path
            }

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@path_id", pathId);
            command.Parameters.AddWithValue("@from", segment.FromTicks);
            command.Parameters.AddWithValue("@to", segment.ToTicks);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ticks = reader.GetInt64(0);
                Integrate(result, previousTicks, ticks, previousValue, bucketTicks);
                previousTicks = ticks;
                previousValue = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            }
        }

        Integrate(result, previousTicks, EpochTicks.ToEpochTicks(window.To), previousValue, bucketTicks);
    }

    private static int CompareByTime(ChainSegment left, ChainSegment right)
    {
        var byStart = left.FromTicks.CompareTo(right.FromTicks);
        return byStart != 0 ? byStart : string.CompareOrdinal(left.PartitionKey, right.PartitionKey);
    }

    // Spreads value * duration over every bucket [fromTicks, toTicks) touches; a null value is unknown
    // and adds nothing.
    private static void Integrate(
        Dictionary<long, BucketPartial> result, long fromTicks, long toTicks, double? value, long bucketTicks)
    {
        if (value is not { } number)
        {
            return;
        }

        var start = fromTicks;
        while (start < toTicks)
        {
            var bucketStart = AlignBucketStart(start, bucketTicks);
            var end = Math.Min(bucketStart + bucketTicks, toTicks);
            var duration = (double)(end - start);
            ref var partial = ref CollectionsMarshal.GetValueRefOrAddDefault(result, bucketStart, out _);
            partial = partial with
            {
                BucketStartTicks = bucketStart,
                WeightedSum = partial.WeightedSum + number * duration,
                TotalDuration = partial.TotalDuration + duration
            };
            start = end;
        }
    }

    private static long AlignBucketStart(long ticks, long bucketTicks)
    {
        var quotient = Math.DivRem(ticks, bucketTicks, out var remainder);
        return (remainder < 0 ? quotient - 1 : quotient) * bucketTicks;
    }

    // First/Last: the earliest (MIN ts) or latest (MAX ts) row per bucket, with its raw value columns.
    private static List<BucketPartial> ReadEdgePartials(
        SqliteConnection connection, long pathId, string aggregation, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        var isFirst = aggregation == HistoryAggregations.First;
        var edge = isFirst ? "MIN(ts)" : "MAX(ts)";

        // SQLite's bare-column rule: with a single MIN or MAX, the other selected columns come from the
        // row that produced it. That gives one grouped pass over the range. The correlated-subquery form
        // this replaces re-ran an unindexable per-row lookup for every candidate row, so a Last query
        // (the default aggregation) over a large partition was quadratic, and it runs while the engine's
        // connection lock is held, which stalls the flush loop and grows the unbounded change queue.
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + FloorBucketExpression("ts") + " AS bucket, " + edge + " AS ts, " +
            "value_long, value_double, value_json FROM history " +
            "WHERE path_id = @path_id AND ts >= @from AND ts < @to " +
            "GROUP BY bucket;";
        command.Parameters.AddWithValue("@path_id", pathId);
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

            // The numeric projection for an edge sample mirrors ToPoint via SqliteValueRouting.Numeric:
            // double/long, plus a ulong-overflow JSON number folded in when the property is ulong.
            var number = SqliteValueRouting.Numeric(new RawRow(ts, longValue, doubleValue, jsonValue), isUlong);

            // A decimal writes both value_double and its exact text into value_json. ToPoint suppresses
            // the JSON when a numeric column is present, so the edge readers must too: otherwise the same
            // decimal property comes back numeric from a raw query and JSON-valued from a bucketed one,
            // and a consumer that dispatches on Json renders it as a discrete state.
            if (doubleValue is not null || longValue is not null)
            {
                jsonValue = null;
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
    private static List<BucketPartial> ReadCountPartials(
        SqliteConnection connection, long pathId, long bucketTicks, long fromTicks, long toTicks)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + FloorBucketExpression("ts") + " AS bucket, COUNT(*) AS cnt FROM history " +
            "WHERE path_id = @path_id AND ts >= @from AND ts < @to GROUP BY bucket ORDER BY bucket;";
        command.Parameters.AddWithValue("@path_id", pathId);
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
    private static List<BucketPartial> ReadNumericPartials(
        SqliteConnection connection, long pathId, bool isUlong,
        long bucketTicks, long fromTicks, long toTicks)
    {
        // The numeric expression: for ulong properties also fold value_json (a JSON number stored as text).
        var numeric = isUlong
            ? "COALESCE(value_double, value_long, CAST(value_json AS REAL))"
            : "COALESCE(value_double, value_long)";

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + FloorBucketExpression("ts") + " AS bucket, " +
            "COUNT(" + numeric + ") AS cnt, " +
            "SUM(" + numeric + ") AS sum_num, " +
            "MIN(" + numeric + ") AS min_num, " +
            "MAX(" + numeric + ") AS max_num, " +
            "SUM(" + numeric + " * " + numeric + ") AS sumsq_num " +
            "FROM history WHERE path_id = @path_id AND ts >= @from AND ts < @to " +
            "GROUP BY bucket ORDER BY bucket;";
        command.Parameters.AddWithValue("@path_id", pathId);
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

    private static string FloorBucketExpression(string timestampExpression) =>
        "((" + timestampExpression + " / @b) - CASE WHEN " + timestampExpression +
        " < 0 AND " + timestampExpression + " % @b <> 0 THEN 1 ELSE 0 END) * @b";
}
