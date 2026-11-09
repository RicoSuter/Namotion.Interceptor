using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs the requests of one input in rule order, each rule at most once, and records every run. A run consumes every
/// pending property trigger of its rule. A request that does not run is not recorded: one that Only if rejects, and,
/// once a rule changed the state, the remaining requests of a property input; the rule shows why and the log says it
/// once per reason, and its property trigger stays pending. The requests of an event still run after a state change,
/// except those of the rules that the change deactivated. A request of a rule that is no longer active is dropped, and
/// so are the requests after a run that a stop canceled. Used by the worker only.
/// </summary>
internal sealed class RunDispatcher(IRuleRunHost host, Automation automation, AutomationStateKeeper keeper, ILogger logger)
{
    private readonly RuleRunner _runner = RuleRunner.Create(automation, logger);

    public async Task<DispatchSummary> DispatchAsync(
        IReadOnlyList<RunRequest> requests, Func<CompiledRule, bool> isActive, CancellationToken cancellationToken)
    {
        var summary = new DispatchSummary();
        foreach (var request in requests)
        {
            // A stop ends the input: the requests after a canceled run do not run.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await DispatchOneAsync(request, isActive, summary, cancellationToken);
        }

        return summary;
    }

    /// <summary>Does not run the requests, for the reason.</summary>
    public void Skip(IReadOnlyList<RunRequest> requests, string reason)
    {
        foreach (var request in requests)
        {
            Skip(request, reason);
        }
    }

    private async Task DispatchOneAsync(
        RunRequest request, Func<CompiledRule, bool> isActive, DispatchSummary summary, CancellationToken cancellationToken)
    {
        if (summary.ChangedState && request.Event is null)
        {
            summary.SkippedForStateChange = true;
            Skip(request, $"the state changed to {host.CurrentState} for the same input");
            return;
        }

        if (!request.Rule.IsActivated || !isActive(request.Rule) || summary.HasRun(request.Rule))
        {
            return;
        }

        var result = await _runner.RunAsync(request, host, cancellationToken);
        host.RunUnlessAbandoned(() => Complete(request, result, summary));
    }

    private void Complete(RunRequest request, RuleRunResult result, DispatchSummary summary)
    {
        if (!result.IsRun)
        {
            Skip(request, result.Message);
            return;
        }

        request.Rule.SkipReason = null;

        // Every trigger of the rule that waits for a run had it now, also one of another request of the same input.
        foreach (var trigger in request.Rule.PropertyTriggers)
        {
            trigger.State.Consume();
        }

        Record(request.Rule, result);
        summary.Add(request.Rule, result);
    }

    // Applies the run to the checkpoint in memory, which the keeper writes later, then sets the outcome and the message
    // of the rule and, last, its run count, so that a history sample of the run count comes after the outcome and the
    // message of the same run.
    private void Record(CompiledRule rule, RuleRunResult result)
    {
        var subject = rule.Rule;
        var run = new RuleCheckpoint
        {
            LastTriggeredAt = subject.LastTriggeredAt,
            RunCount = (subject.RunCount ?? 0) + 1,
            LastRunOutcome = result.Outcome,
            LastRunMessage = result.Message
        };

        keeper.RecordRun(rule.Key, run, automation.TimeProvider.GetUtcNow());
        subject.LastRunOutcome = run.LastRunOutcome;
        subject.LastRunMessage = run.LastRunMessage;
        subject.RunCount = run.RunCount;
    }

    private void Skip(RunRequest request, string reason)
    {
        var rule = request.Rule;
        if (string.Equals(rule.SkipReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        rule.SkipReason = reason;
        logger.LogInformation(
            "The {Source} of rule {Rule} in automation {Automation} requested a run that did not run: {Reason}.",
            request.Source, rule.Name, host.Name, reason);
    }
}
