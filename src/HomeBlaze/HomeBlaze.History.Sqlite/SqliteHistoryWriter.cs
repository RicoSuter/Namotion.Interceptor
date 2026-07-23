using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// A value sample queued for flush: its path, timestamp, the routed <see cref="Row"/>, and the column
/// kind plus ulong flag persisted into <c>path_meta</c>.
/// </summary>
internal readonly record struct PendingSample(
    string Path, DateTimeOffset Timestamp, Row Row, ValueColumn Column, bool IsUlong);

/// <summary>
/// A recorded path move queued for flush, persisted into <c>moves.db</c> and replayed when resolving a
/// queried path's move chain.
/// </summary>
internal readonly record struct MoveRecord(DateTimeOffset Timestamp, string FromPath, string ToPath);

/// <summary>
/// Pure write SQL for the SQLite history engine: the batched <c>INSERT OR REPLACE</c> into a partition
/// file (with its <c>path_meta</c> upsert) and the moves insert. These helpers operate on connections
/// the engine opens and passes in; they never lock, never open or cache connections, and hold no state.
/// The engine calls them while holding its connection lock, and owns the pending buffers plus the
/// re-queue-on-failure orchestration.
/// </summary>
internal static class SqliteHistoryWriter
{
    // Writes one partition's batch in a single transaction: each sample's row into history and its
    // column metadata into path_meta. Returns the maximum committed epoch-tick timestamp in the batch.
    public static long WritePartition(SqliteConnection connection, IReadOnlyList<PendingSample> samples)
    {
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

    // Persists queued moves into moves.db in a single transaction (the engine passes the moves connection).
    public static void WriteMoves(SqliteConnection movesConnection, IReadOnlyList<MoveRecord> moves)
    {
        using var transaction = movesConnection.BeginTransaction();
        using var insert = movesConnection.CreateCommand();
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
}
