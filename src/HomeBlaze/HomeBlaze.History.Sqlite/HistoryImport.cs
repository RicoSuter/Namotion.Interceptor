namespace HomeBlaze.History.Sqlite;

/// <summary>
/// A historical sample handed to <see cref="SqliteHistoryStore.Import"/>: the instant it was observed and
/// the value, typed like the property it is imported for.
/// </summary>
public readonly record struct HistoryImportSample(DateTimeOffset Timestamp, object? Value);

/// <summary>
/// The outcome of one <see cref="SqliteHistoryStore.Import"/> call.
/// </summary>
/// <param name="InsertedCount">Rows written.</param>
/// <param name="IgnoredCount">Samples skipped because the store already held a row for the path at that instant.</param>
/// <param name="OversizeCount">String values replaced by the oversize placeholder.</param>
public readonly record struct HistoryImportResult(long InsertedCount, long IgnoredCount, long OversizeCount);
