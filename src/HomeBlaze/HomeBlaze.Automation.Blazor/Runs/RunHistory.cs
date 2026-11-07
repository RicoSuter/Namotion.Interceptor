using System.Text.Json;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Reads the recent runs of rules from history. Every sample of the <c>RunCount</c> of a rule that follows a known count
/// is a run, also when two runs in a row are the same, and its outcome and message are those held at the time of the
/// sample. A count that follows no sample or a null sample is the count restored when the automation started, and a count that
/// is equal or lower is a counter reset, neither is a run.
/// </summary>
internal static class RunHistory
{
    private const string Separator = "/";

    /// <summary>
    /// Gets the runs between the instants, newest first, at most the given number over all rules, and whether the history
    /// lags behind the rules: a rule whose current run count is above the newest count stored. History stores buffer their
    /// writes, so a run that has just happened is not stored yet. Reads the newest counts of each rule and the outcome and
    /// message only of the runs it returns.
    /// </summary>
    public static async Task<RunHistoryResult> LoadAsync(
        IReadOnlyCollection<IHistoryStore> stores,
        IReadOnlyList<RunSource> rules,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxRuns,
        CancellationToken cancellationToken)
    {
        if (stores.Count == 0 || rules.Count == 0 || from >= to || maxRuns <= 0)
        {
            return new RunHistoryResult([], false);
        }

        var runTimes = new List<(DateTimeOffset At, RunSource Rule)>();
        var isBehind = false;
        foreach (var rule in rules)
        {
            var (times, storedCount) = await LoadRunTimesAsync(stores, rule, from, to, maxRuns, cancellationToken);
            runTimes.AddRange(times.Select(at => (at, rule)));
            isBehind |= rule.RunCount is > 0 && rule.RunCount > (storedCount ?? 0);
        }

        var runs = new List<RunEntry>();
        foreach (var (at, rule) in runTimes.OrderByDescending(run => run.At).Take(maxRuns))
        {
            runs.Add(await ReadRunAsync(stores, rule, at, cancellationToken));
        }

        return new RunHistoryResult(runs, isBehind);
    }

    // The query keeps the newest counts, one more than the runs it may return: when it is full, its oldest count is the
    // count before the others, and otherwise the count before the window is read.
    private static async Task<(List<DateTimeOffset> Times, double? StoredCount)> LoadRunTimesAsync(
        IReadOnlyCollection<IHistoryStore> stores, RunSource rule, DateTimeOffset from, DateTimeOffset to, int maxRuns, CancellationToken cancellationToken)
    {
        var countPath = rule.RulePath + Separator + nameof(AutomationRule.RunCount);
        var series = await stores.QueryHistoryAsync(new HistoryQuery(countPath, from, to, MaxPoints: maxRuns + 1), cancellationToken);
        var points = series.Points;
        var isFull = points.Length > maxRuns;
        var previous = isFull ? points[0] : await stores.GetValueAtAsync(countPath, from.AddTicks(-1), cancellationToken);
        var times = new List<DateTimeOffset>();
        foreach (var point in points.AsSpan()[(isFull ? 1 : 0)..])
        {
            if (previous?.Number is { } before && point.Number is { } after && after > before)
            {
                times.Add(point.Timestamp);
            }

            previous = point;
        }

        return (times, points.Length > 0 ? points[^1].Number : previous?.Number);
    }

    private static async Task<RunEntry> ReadRunAsync(
        IReadOnlyCollection<IHistoryStore> stores, RunSource rule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var outcome = await stores.GetValueAtAsync(rule.RulePath + Separator + nameof(AutomationRule.LastRunOutcome), at, cancellationToken);
        var message = await stores.GetValueAtAsync(rule.RulePath + Separator + nameof(AutomationRule.LastRunMessage), at, cancellationToken);
        return new RunEntry(at, rule.RuleName, ReadOutcome(outcome), ReadText(message));
    }

    private static AutomationRunOutcome? ReadOutcome(HistoryPoint? point) =>
        ReadText(point) is { } text && Enum.TryParse<AutomationRunOutcome>(text, out var outcome) ? outcome : null;

    private static string? ReadText(HistoryPoint? point) =>
        point?.Json is { ValueKind: JsonValueKind.String } json ? json.GetString() : null;
}

/// <summary>The runs <see cref="RunHistory"/> read, newest first, and whether the history lags behind the live run counts.</summary>
internal sealed record RunHistoryResult(IReadOnlyList<RunEntry> Runs, bool IsBehind);
