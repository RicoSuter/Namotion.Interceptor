namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>Selects the runs of one rule, or all runs without a selection.</summary>
internal static class RunFilter
{
    public static IReadOnlyList<RunEntry> Apply(IReadOnlyList<RunEntry> runs, string? selectedRuleName) =>
        selectedRuleName is null ? runs : runs.Where(run => string.Equals(run.RuleName, selectedRuleName, StringComparison.Ordinal)).ToArray();
}
