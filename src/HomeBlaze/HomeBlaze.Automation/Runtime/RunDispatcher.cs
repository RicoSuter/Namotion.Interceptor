using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs the requests of one input in rule order, each rule at most once, and records every run. A run consumes every
/// pending property trigger of its rule. A request that does not run is not recorded: one
/// that Only if rejects, and, once a rule changed the state, the remaining requests of the input; the rule shows why
/// and the log says it once per reason, and its property trigger stays pending. A request of a rule that is no longer
/// active is dropped, and so are the requests after a run that a stop canceled. Used by the worker only.
/// </summary>
internal sealed class RunDispatcher(IRuleRunHost host, Automation automation, AutomationStateKeeper keeper, ILogger logger)
{
    private readonly RuleRunner _runner = RuleRunner.Create(automation, logger);
    private readonly RunRecorder _recorder = new(keeper, automation.TimeProvider);

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
        if (summary.ChangedState)
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
        if (host.IsAbandoned)
        {
            return;
        }

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

        _recorder.Record(request.Rule, result);
        summary.Add(request.Rule, result);
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
