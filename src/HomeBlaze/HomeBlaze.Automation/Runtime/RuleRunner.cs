using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs one request of one rule: checks Only if against the graph and does not run a request it rejects, describes the
/// rendered actions of a dry run, or, in the mode <see cref="AutomationRuleMode.On"/> only, sets the trigger time and
/// runs the actions in order under their error policy until the automation stops. Logs every failed action with the
/// automation, the rule, the action and the trigger.
/// </summary>
internal sealed class RuleRunner(ActionExecutor executor, SubjectPathResolver resolver, TimeProvider timeProvider, ILogger logger)
{
    public static RuleRunner Create(Automation automation, ILogger logger) =>
        new(ActionExecutor.For(automation), automation.PathResolver, automation.TimeProvider, logger);

    public async Task<RuleRunResult> RunAsync(RunRequest request, IRuleRunHost host, CancellationToken cancellationToken)
    {
        if (request.Rule.Mode is not (AutomationRuleMode.DryRun or AutomationRuleMode.On))
        {
            return RuleRunResult.NotRun($"the mode {request.Rule.Mode} is not defined");
        }

        if (CheckOnlyIf(request) is { } rejection)
        {
            return RuleRunResult.NotRun(rejection);
        }

        if (request.Rule.Mode == AutomationRuleMode.DryRun)
        {
            return RuleRunResult.DryRun("Would run " + DescribeActions(request, host));
        }

        request.Rule.Rule.LastTriggeredAt = timeProvider.GetUtcNow();
        try
        {
            return await ExecuteActionsAsync(request, host, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // An operation may turn its cancellation into another exception, such as a client disposed on cancellation.
            return RuleRunResult.Canceled("Canceled: the automation stopped");
        }
    }

    private string DescribeActions(RunRequest request, IRuleRunHost host)
    {
        var scope = CreateScope(request, host);
        return string.Join("; ", request.Rule.Actions.Select(action => executor.Describe(action, scope)));
    }

    // Returns why Only if rejects the request, or null when it passes.
    private string? CheckOnlyIf(RunRequest request)
    {
        if (request.Rule.OnlyIf is not { } onlyIf)
        {
            return null;
        }

        var evaluation = CreateEvaluation(request);
        return evaluation.EvaluateCondition(onlyIf) switch
        {
            true => null,
            false => "Only if is false",
            null => evaluation.MissingPaths.Count > 0
                ? $"Only if is unknown, missing {string.Join(", ", evaluation.MissingPaths)}"
                : "Only if is unknown"
        };
    }

    private async Task<RuleRunResult> ExecuteActionsAsync(RunRequest request, IRuleRunHost host, CancellationToken cancellationToken)
    {
        var rule = request.Rule;
        var log = new RunLog(rule.Name);

        // Per action, so that a run that leaves the state and enters it again changed it, as two runs that do so would.
        var changedState = false;
        List<string>? passedStates = null;
        for (var index = 0; index < rule.Actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = rule.Actions[index];
            var stateBefore = host.CurrentState;
            var result = await ExecuteAsync(action, request, host, cancellationToken);
            if (!string.Equals(stateBefore, host.CurrentState, StringComparison.Ordinal))
            {
                if (changedState && stateBefore is not null)
                {
                    (passedStates ??= []).Add(stateBefore);
                }

                changedState = true;
            }

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

        return log.ToResult(changedState, passedStates ?? (IReadOnlyList<string>)[]);
    }

    private async Task<ActionResult> ExecuteAsync(AutomationAction action, RunRequest request, IRuleRunHost host, CancellationToken cancellationToken) => action switch
    {
        InvokeOperationAction invoke => await executor.InvokeOperationAsync(invoke, CreateScope(request, host), cancellationToken),
        SetPropertyAction set => executor.SetProperty(set, CreateScope(request, host)),
        ChangeStateAction change => ChangeState(change, host),
        _ => ActionResult.Failure("the type of the action is unknown")
    };

    private static ActionResult ChangeState(ChangeStateAction change, IRuleRunHost host) =>
        host.ChangeState(change.TargetState) is { } error ? ActionResult.Failure(error) : ActionResult.Success(ActionExecutor.DescribeChangeState(change));

    private ActionScope CreateScope(RunRequest request, IRuleRunHost host) =>
        new(host.Automation, request.Rule.Rule, CreateEvaluation(request), request.Rule.AllowsEvent);

    // A new evaluation for Only if and per action, so that each reads now when it is evaluated.
    private AutomationEvaluation CreateEvaluation(RunRequest request) =>
        new(resolver, request.Rule.Rule, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone, request.Event);
}
