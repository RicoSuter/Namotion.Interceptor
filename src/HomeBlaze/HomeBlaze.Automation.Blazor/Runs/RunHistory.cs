using System.Text.Json;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Reads the recent runs of rules from history. Every sample of the <c>RunCount</c> of a rule that follows a known count
/// is a run, also when two runs in a row are the same, and its outcome and message are those held at the time of the
/// sample. A count that follows no sample or a null sample is the count restored when the automation started, not a run.
/// </summary>
public static class RunHistory
{
    private const string Separator = "/";

    /// <summary>Gets the runs between the instants, newest first, at most the given number over all rules.</summary>
    public static async Task<IReadOnlyList<RunEntry>> LoadAsync(
        IReadOnlyCollection<IHistoryStore> stores,
        IReadOnlyList<RunSource> rules,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxRuns,
        CancellationToken cancellationToken)
    {
        if (stores.Count == 0 || rules.Count == 0 || from >= to)
        {
            return [];
        }

        var runs = new List<RunEntry>();
        foreach (var rule in rules)
        {
            runs.AddRange(await LoadRuleAsync(stores, rule, from, to, maxRuns, cancellationToken));
        }

        return [.. runs.OrderByDescending(run => run.At).Take(maxRuns)];
    }

    private static async Task<IReadOnlyList<RunEntry>> LoadRuleAsync(
        IReadOnlyCollection<IHistoryStore> stores, RunSource rule, DateTimeOffset from, DateTimeOffset to, int maxRuns, CancellationToken cancellationToken)
    {
        var countPath = rule.RulePath + Separator + nameof(AutomationRule.RunCount);
        var series = await stores.QueryHistoryAsync(new HistoryQuery(countPath, from, to), cancellationToken);
        var previous = await stores.GetValueAtAsync(countPath, from.AddTicks(-1), cancellationToken);
        var runTimes = new List<DateTimeOffset>();
        foreach (var point in series.Points)
        {
            if (previous?.Number is not null && point.Number is not null)
            {
                runTimes.Add(point.Timestamp);
            }

            previous = point;
        }

        var runs = new List<RunEntry>();
        foreach (var at in runTimes.TakeLast(maxRuns))
        {
            runs.Add(await ReadRunAsync(stores, rule, at, cancellationToken));
        }

        return runs;
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
