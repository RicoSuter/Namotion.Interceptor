using HomeBlaze.Automation.Persistence;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Records a run: applies it to the checkpoint in memory, which <see cref="AutomationStateKeeper"/> writes later, then
/// sets the outcome and the message of the rule and, last, its run count, so that a history sample of the run count
/// comes after the outcome and the message of the same run.
/// </summary>
internal sealed class RunRecorder(AutomationStateKeeper keeper, TimeProvider timeProvider)
{
    public void Record(CompiledRule rule, RuleRunResult result)
    {
        var outcome = result.Outcome ?? throw new ArgumentException("A request that did not run is not recorded.", nameof(result));
        var subject = rule.Rule;
        var run = new RuleCheckpoint
        {
            LastTriggeredAt = subject.LastTriggeredAt,
            RunCount = (subject.RunCount ?? 0) + 1,
            LastRunOutcome = outcome,
            LastRunMessage = result.Message
        };

        keeper.RecordRun(rule.Key, run, timeProvider.GetUtcNow());
        subject.LastRunOutcome = run.LastRunOutcome;
        subject.LastRunMessage = run.LastRunMessage;
        subject.RunCount = run.RunCount;
    }
}
