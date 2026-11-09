namespace HomeBlaze.Automation.Runtime;

/// <summary>Collects the actions of a run that ran and that failed, and turns them into its result.</summary>
internal sealed class RunLog(string ruleName)
{
    private readonly List<string> _ran = [];
    private readonly List<string> _failures = [];
    private bool _isStopped;

    public void Succeeded(string description) => _ran.Add(description);

    public void Failed(string label, string error) => _failures.Add($"{label} failed: {error}");

    public void Stop() => _isStopped = true;

    /// <summary>Gets the result of the run, which changed the state when <paramref name="changedState"/>, see <see cref="RuleRunResult.PassedStates"/>.</summary>
    public RuleRunResult ToResult(bool changedState, IReadOnlyList<string> passedStates)
    {
        if (_failures.Count == 0)
        {
            return new RuleRunResult(AutomationRunOutcome.Succeeded, RuleRunResult.Shorten("Ran " + string.Join("; ", _ran)), changedState, null) { PassedStates = passedStates };
        }

        var message = string.Join("; ", _failures) + (_isStopped ? "; the remaining actions were skipped" : string.Empty);
        var outcome = _isStopped ? AutomationRunOutcome.StoppedAfterError : AutomationRunOutcome.CompletedWithErrors;
        return new RuleRunResult(outcome, RuleRunResult.Shorten(message), changedState, $"Rule '{ruleName}': {_failures[^1]}") { PassedStates = passedStates };
    }
}
