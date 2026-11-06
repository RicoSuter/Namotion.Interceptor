using HomeBlaze.Automation.Persistence;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Records a run: writes it to the checkpoint first, then sets the outcome and the message of the rule and, last, its run
/// count, so that a history sample of the run count comes after the outcome and the message of the same run.
/// </summary>
internal sealed class RunRecorder(AutomationStateKeeper keeper)
{
    /// <summary>Records the run and returns the error of the save, or null.</summary>
    public string? Record(CompiledRule rule, RuleRunResult result)
    {
        var subject = rule.Rule;
        var run = new RuleCheckpoint
        {
            LastTriggeredAt = subject.LastTriggeredAt,
            RunCount = (subject.RunCount ?? 0) + 1,
            LastRunOutcome = result.Outcome,
            LastRunMessage = result.Message
        };

        var saveError = keeper.TryPersist(AutomationStateKeeper.WithRule(keeper.Persisted, rule.Key, run));
        subject.LastRunOutcome = run.LastRunOutcome;
        subject.LastRunMessage = saveError is null
            ? run.LastRunMessage
            : RuleRunResult.Shorten($"{run.LastRunMessage}; the run was not saved: {saveError}");
        subject.RunCount = run.RunCount;
        return saveError is null ? null : $"Rule '{rule.Name}': the run was not saved: {saveError}";
    }
}
