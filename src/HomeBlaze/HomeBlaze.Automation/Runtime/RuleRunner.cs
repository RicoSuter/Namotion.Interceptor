using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs one request of one rule: checks the guard against the graph, records a dry run with the rendered actions, or
/// sets the trigger time and runs the actions in order under their error policy. Logs every failed action with the
/// automation, the rule, the action and the trigger.
/// </summary>
internal sealed class RuleRunner(ActionExecutor executor, SubjectPathResolver resolver, TimeProvider timeProvider, ILogger logger)
{
    public async Task<RuleRunResult> RunAsync(RunRequest request, IRuleRunHost host, CancellationToken cancellationToken)
    {
        if (CheckGuard(request) is { } rejection)
        {
            return rejection;
        }

        if (request.Rule.Mode == AutomationRuleMode.DryRun)
        {
            return RuleRunResult.DryRun("Would run " + string.Join("; ", Describe(request, host)));
        }

        request.Rule.Rule.LastTriggeredAt = timeProvider.GetUtcNow();
        try
        {
            return await ExecuteActionsAsync(request, host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RuleRunResult.Canceled("Canceled: the automation stopped");
        }
    }

    /// <summary>Describes the actions with their rendered arguments, without running them.</summary>
    public IEnumerable<string> Describe(RunRequest request, IRuleRunHost host)
    {
        var scope = CreateScope(request, host);
        return request.Rule.Actions.Select(action => Describe(executor, action, scope)).ToList();
    }

    public static string Describe(ActionExecutor executor, AutomationAction action, ActionScope scope) => action switch
    {
        InvokeOperationAction invoke => executor.Describe(invoke, scope),
        SetPropertyAction set => executor.Describe(set, scope),
        ChangeStateAction change => DescribeChangeState(change),
        _ => "an action of an unknown type"
    };

    private static string DescribeChangeState(ChangeStateAction change) => $"ChangeState({change.TargetState})";

    private RuleRunResult? CheckGuard(RunRequest request)
    {
        if (request.Rule.Guard is not { } guard)
        {
            return null;
        }

        var evaluation = CreateEvaluation(request);
        return evaluation.EvaluateCondition(guard) switch
        {
            true => null,
            false => RuleRunResult.Rejected("Rejected: the guard is false"),
            null => RuleRunResult.Rejected(evaluation.MissingPaths.Count > 0
                ? $"Rejected: the guard is unknown, missing {string.Join(", ", evaluation.MissingPaths)}"
                : "Rejected: the guard is unknown")
        };
    }

    private async Task<RuleRunResult> ExecuteActionsAsync(RunRequest request, IRuleRunHost host, CancellationToken cancellationToken)
    {
        var rule = request.Rule;
        var log = new RunLog(rule.Name);
        var stateBefore = host.CurrentState;
        for (var index = 0; index < rule.Actions.Count; index++)
        {
            var action = rule.Actions[index];
            var result = await ExecuteAsync(action, request, host, cancellationToken);
            if (result.IsSuccess)
            {
                log.Succeeded(result.Message);
                continue;
            }

            var label = AutomationCompiler.DescribeAction(action, index);
            logger.LogWarning(
                "The {Action} of rule {Rule} in automation {Automation} failed after {Source}: {Error}",
                label, rule.Name, host.Name, request.Source, result.Message);
            log.Failed(label, result.Message);
            if (action.OnError == ActionErrorPolicy.StopRule)
            {
                log.Stop();
                break;
            }
        }

        return log.ToResult(changedState: !string.Equals(stateBefore, host.CurrentState, StringComparison.Ordinal));
    }

    private async Task<ActionResult> ExecuteAsync(AutomationAction action, RunRequest request, IRuleRunHost host, CancellationToken cancellationToken) => action switch
    {
        InvokeOperationAction invoke => await executor.InvokeOperationAsync(invoke, CreateScope(request, host), cancellationToken),
        SetPropertyAction set => executor.SetProperty(set, CreateScope(request, host)),
        ChangeStateAction change => ChangeState(change, host),
        _ => ActionResult.Failure("the type of the action is unknown")
    };

    private static ActionResult ChangeState(ChangeStateAction change, IRuleRunHost host) =>
        host.ChangeState(change.TargetState) is { } error ? ActionResult.Failure(error) : ActionResult.Success(DescribeChangeState(change));

    private ActionScope CreateScope(RunRequest request, IRuleRunHost host) =>
        new(host.Automation, request.Rule.Rule, CreateEvaluation(request), request.Rule.AllowsEvent);

    // A new evaluation per guard and per action, so that each reads now when it is evaluated.
    private AutomationEvaluation CreateEvaluation(RunRequest request) =>
        new(resolver, request.Rule.Rule, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone, request.Event);
}
