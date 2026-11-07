using HomeBlaze.Abstractions;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Loads the recent runs the page of an automation lists: the runs of the last seven days from the history stores of the
/// graph, or without a history store, the last run of each rule.
/// </summary>
internal static class RecentRuns
{
    private const int MaxRuns = 20;

    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>
    /// Loads the runs. A failure of history is part of the result; a canceled load throws an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<RecentRunsResult> LoadAsync(
        Automation automation, SubjectPathResolver pathResolver, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IHistoryStore[] stores = ((IInterceptorSubject)automation).Context.TryGetService<ISubjectRegistry>()?.KnownSubjects.Keys.OfType<IHistoryStore>().ToArray() ?? [];
        if (stores.Length == 0)
        {
            var lastRuns = RunHistory.GetLastRuns(automation.GetRulesInOrder().Select(pair => (pair.Value.Name ?? pair.Key, pair.Value.LastRunOutcome, pair.Value.LastRunMessage)));
            return new RecentRunsResult(false, lastRuns, null, false);
        }

        try
        {
            var result = await RunHistory.LoadAsync(stores, GetRunSources(automation, pathResolver), now - Window, now, MaxRuns, cancellationToken);
            return new RecentRunsResult(true, result.Runs, null, result.IsBehind);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            return new RecentRunsResult(true, [], $"The runs could not be read from history: {exception.Message}", false);
        }
    }

    /// <summary>
    /// A hash of the names and run counts of the rules, which changes with every run, so that the page that renders it knows
    /// when to load the runs again. A hash instead of a text, because the page renders on every change of a status.
    /// </summary>
    public static int GetKey(Automation automation)
    {
        var hash = new HashCode();
        foreach (var (key, rule) in automation.Rules)
        {
            hash.Add(key);
            hash.Add(rule.Name);
            hash.Add(rule.RunCount);
        }

        return hash.ToHashCode();
    }

    private static IReadOnlyList<RunSource> GetRunSources(Automation automation, SubjectPathResolver pathResolver) =>
    [
        .. automation.GetRulesInOrder()
            .Select(pair => (pair.Key, pair.Value, Path: pathResolver.GetPath(pair.Value, PathStyle.Canonical)))
            .Where(rule => rule.Path is not null)
            .Select(rule => new RunSource(rule.Value.Name ?? rule.Key, rule.Path!, rule.Value.RunCount))
    ];
}

/// <summary>
/// The runs <see cref="RecentRuns"/> loaded, newest first, whether they come from history, why history could not be read,
/// and whether history lags behind the live run counts.
/// </summary>
internal sealed record RecentRunsResult(bool HasHistory, IReadOnlyList<RunEntry> Runs, string? Error, bool IsBehind)
{
    public string Caption => HasHistory
        ? "From history: the runs of the last seven days."
        : "Without a history store: the last run of each rule.";
}
