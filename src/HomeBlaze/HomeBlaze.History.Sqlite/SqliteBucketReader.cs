using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Pure bucketed-aggregation read SQL for the SQLite history engine: the bucketed query orchestration
/// plus the four per-partition partial readers (first/last edge, count, numeric reductions, and the
/// time-weighted-average <c>LEAD</c> scan). It reuses <see cref="SqliteHistoryReader"/> for move-chain
/// resolution and column metadata, <see cref="SqliteValueRouting"/> for value mapping, and
/// <see cref="BucketAssembler"/> for final assembly. Every method takes a <see cref="SqliteReadContext"/>
/// (the engine's open-connection delegates plus partition layout); these helpers never lock and never
/// touch the engine's connection cache. The engine calls them while holding its connection lock.
/// </summary>
internal static class SqliteBucketReader
{
    public static HistorySeries QueryBucketed(
        SqliteReadContext context, HistoryQuery query, Func<string, DateTimeOffset, HistoryPoint?> getSampleAtOrBefore)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;
        var bucketTicks = bucket.Ticks;

        // Resolve the move chain once: each leg owns its [ValidFrom, ValidTo) slice of time and stores its
        // samples under its own path. With no moves this is a single unbounded leg under query.PropertyPath.
        var chain = SqliteHistoryReader.ResolveChain(context, query.PropertyPath);

        // Resolve the stored column kind and ulong flag from path_meta along the chain (the SQLite
        // equivalent of the InMemory buffer's Column/IsUlong, which uses the first buffer in the chain).
        // A numeric aggregation on a json-stored, non-ulong property (decimal/string/enum) is not
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
        var alignedFrom = BucketAlignment.BucketStart(query.From, bucket);

        // Expand the chain into concrete (path, tickWindow) segments over existing partition files.
        var segments = BuildChainSegments(context, chain, alignedFrom, query.To);

        Dictionary<long, BucketPartial> partials;
        if (aggregation == HistoryAggregations.TimeWeightedAverage)
        {
            // TWA needs LEAD to see across partition files AND across legs: a sample is held until the NEXT
            // sample, which may live in a later partition file or a later leg's path. So read the numeric
            // samples over the UNION ALL of all segments in one ordered query (others ATTACHed), mirroring
            // InMemory which merges every leg's samples into one ascending list before integrating.
            partials = ReadTimeWeightedAveragePartials(context, segments, isUlong, bucketTicks);
        }
        else
        {
            partials = new Dictionary<long, BucketPartial>();
            foreach (var segment in segments)
            {
                var connection = context.OpenPartition(segment.PartitionKey);
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
        // Mirrors InMemoryHistoryStore.QueryBucketed.
        if (aggregation == HistoryAggregations.TimeWeightedAverage)
        {
            carrySeedNumber = query.CarrySeed?.Number
                ?? getSampleAtOrBefore(query.PropertyPath, alignedFrom)?.Number;
        }

        return BucketAssembler.Assemble(query, partials, carrySeedNumber, carrySeedJson);
    }

    // Expands a chain into concrete (path, partitionKey, tickWindow) segments over EXISTING partition files.
    // For each leg, the query window [from, to) is intersected with the leg's [ValidFrom, ValidTo); the
    // intersection is split across the partition files it overlaps. With no moves this is the single-path,
    // multi-partition segment set used before move routing.
    private static List<ChainSegment> BuildChainSegments(
        SqliteReadContext context, List<ChainLeg> chain, DateTimeOffset from, DateTimeOffset to)
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
            foreach (var key in context.PartitionKeysOverlapping(legFrom, legTo))
            {
                if (context.PartitionFileExists(key))
                {
                    segments.Add(new ChainSegment(leg.Path, key, fromTicks, toTicks));
                }
            }
        }

        return segments;
    }

    private static bool IsNumericAggregation(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    // One grouped query per partition producing the partials for the requested aggregation. Only the
    // columns the aggregation needs are fetched. The bucket key is (ts/@b)*@b on epoch ticks, which equals
    // BucketAlignment.BucketStart for the same bucket size.
    private static IEnumerable<BucketPartial> ReadPartials(
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
    // over [ts, min(nextNumericTs, bucketEnd)); the next numeric sample bridges across non-numeric/null rows
    // because those are filtered out of the ordered set (mirroring InMemory, where a null sample leaves the
    // held value unchanged). The leading interval [bucketStart, firstNumericTs) and empty-bucket carry are
    // supplied by BucketAssembler, which also needs FirstTicks (the leading-interval boundary) and LastNumber
    // (the carry-advance value).
    //
    // Unlike the other aggregations, TWA cannot be computed per segment then summed: a sample is held until the
    // NEXT sample, which may live in a LATER partition file OR a LATER move-chain leg's path, so the integrator
    // must see one ascending stream over ALL segments (mirroring InMemory, which merges every leg's samples
    // into one ascending list before integrating). The old reader produced that stream by ATTACHing every
    // distinct partition file onto one connection and UNION ALL-ing them, but SQLite caps attached databases at
    // 10 (SQLITE_MAX_ATTACHED), so a range spanning 11+ partition files threw "too many attached databases".
    // Instead, read each segment's numeric samples on its own partition connection and merge them in memory:
    // the segments cover disjoint (path, time) slices (partition files cover disjoint time ranges and move-chain
    // legs cover disjoint validity windows), so a single ascending sort over the union faithfully reconstructs
    // the ordered scan the integrator needs, with no attach cap.
    //
    // The value is read as REAL so value * duration is floating point: tick products are huge (value ~tens
    // times ~10^8 ticks per 10s) and an integer sum could overflow; the weightedSum/totalDuration ratio is
    // unit-free.
    private static Dictionary<long, BucketPartial> ReadTimeWeightedAveragePartials(
        SqliteReadContext context, IReadOnlyList<ChainSegment> segments, bool isUlong, long bucketTicks)
    {
        var result = new Dictionary<long, BucketPartial>();
        if (segments.Count == 0)
        {
            return result;
        }

        var numeric = isUlong
            ? "CAST(COALESCE(value_double, value_long, CAST(value_json AS REAL)) AS REAL)"
            : "CAST(COALESCE(value_double, value_long) AS REAL)";

        // Collect every segment's numeric (ts, value) rows. Each segment reads only its own path over its tick
        // window from its own partition file, so a row belongs to exactly one segment (no duplication).
        var samples = new List<(long Ticks, double Value)>();
        foreach (var segment in segments)
        {
            var connection = context.OpenPartition(segment.PartitionKey);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ts, " + numeric + " AS v FROM history " +
                "WHERE path = @path AND ts >= @from AND ts < @to AND " + numeric + " IS NOT NULL;";
            command.Parameters.AddWithValue("@path", segment.Path);
            command.Parameters.AddWithValue("@from", segment.FromTicks);
            command.Parameters.AddWithValue("@to", segment.ToTicks);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                samples.Add((reader.GetInt64(0), reader.GetDouble(1)));
            }
        }

        if (samples.Count == 0)
        {
            return result;
        }

        // Ascending order by ts reproduces the old "ORDER BY ts" over the UNION ALL. The merged stream is the
        // single ordered scan the in-bucket integral below needs.
        samples.Sort(static (left, right) => left.Ticks.CompareTo(right.Ticks));

        // In-bucket integral, equivalent to the old window-function SQL: each sample's value is held over
        // [ts, min(nextTs, bucketEnd)); the next sample is the global successor (held across buckets, files, and
        // legs), defaulting to bucketEnd for the final sample. Per bucket: FirstTicks is the earliest sample's
        // ts (the leading-interval boundary) and LastNumber is the latest sample's value (the carry-advance).
        for (var index = 0; index < samples.Count; index++)
        {
            var (ticks, value) = samples[index];
            var bucketStart = (ticks / bucketTicks) * bucketTicks;
            var bucketEnd = bucketStart + bucketTicks;
            var nextTicks = index + 1 < samples.Count ? samples[index + 1].Ticks : bucketEnd;
            var duration = (double)((nextTicks < bucketEnd ? nextTicks : bucketEnd) - ticks);

            if (result.TryGetValue(bucketStart, out var partial))
            {
                // Ascending order: this is a later sample in the same bucket, so it becomes the new LastNumber.
                result[bucketStart] = partial with
                {
                    WeightedSum = partial.WeightedSum + value * duration,
                    TotalDuration = partial.TotalDuration + duration,
                    LastNumber = value
                };
            }
            else
            {
                // First (earliest) sample seen for this bucket: it is both FirstTicks and (so far) LastNumber.
                result[bucketStart] = new BucketPartial(
                    bucketStart, 0, null, null, null, null,
                    ticks, null, null, null, value, null, value * duration, duration);
            }
        }

        return result;
    }

    // First/Last: the earliest (MIN ts) or latest (MAX ts) row per bucket, with its raw value columns.
    private static List<BucketPartial> ReadEdgePartials(
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

            // The numeric projection for an edge sample mirrors ToPoint via SqliteValueRouting.Numeric:
            // double/long, plus a ulong-overflow JSON number folded in when the property is ulong.
            var number = SqliteValueRouting.Numeric(new RawRow(ts, longValue, doubleValue, jsonValue), isUlong);

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
    private static List<BucketPartial> ReadNumericPartials(
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
}
