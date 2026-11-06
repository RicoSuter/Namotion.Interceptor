using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs the requests of one input in rule order and records every run. Once a rule changed the state, the remaining
/// requests of the input are recorded as canceled; a request of a rule that is no longer active is dropped. Used by the
/// worker only.
/// </summary>
internal sealed class RunDispatcher(IRuleRunHost host, AutomationServices services, AutomationStateKeeper keeper, ILogger logger)
{
    private readonly RuleRunner _runner = RuleRunner.Create(services, logger);
    private readonly RunRecorder _recorder = new(keeper);

    public async Task<DispatchSummary> DispatchAsync(
        IReadOnlyList<RunRequest> requests, Func<CompiledRule, bool> isActive, CancellationToken cancellationToken)
    {
        var summary = new DispatchSummary();
        foreach (var request in requests)
        {
            await DispatchOneAsync(request, isActive, summary, cancellationToken);
        }

        return summary;
    }

    private async Task DispatchOneAsync(
        RunRequest request, Func<CompiledRule, bool> isActive, DispatchSummary summary, CancellationToken cancellationToken)
    {
        if (summary.ChangedState)
        {
            Record(request.Rule, RuleRunResult.Canceled($"Canceled: the state changed to {host.CurrentState} for the same input"), summary);
            return;
        }

        if (request.Rule.IsActivated && isActive(request.Rule))
        {
            Record(request.Rule, await _runner.RunAsync(request, host, cancellationToken), summary);
        }
    }

    private void Record(CompiledRule rule, RuleRunResult result, DispatchSummary summary) =>
        summary.Add(result, _recorder.Record(rule, result));
}
