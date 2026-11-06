using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The rules of an automation on its worker: applies configurations, activates and deactivates rules by mode, enabled
/// flag and state through <see cref="AutomationTriggers"/>, has <see cref="RunDispatcher"/> run the requests of one
/// input, and keeps the status. Used by the worker only.
/// </summary>
internal sealed class AutomationEngine : IRuleRunHost
{
    private readonly Automation _automation;
    private readonly AutomationStateKeeper _keeper;
    private readonly AutomationTriggers _triggers;
    private readonly RunDispatcher _dispatcher;
    private readonly StateTransitions _states;
    private readonly AutomationStatusReporter _status;
    private readonly ILogger _logger;
    private CompiledAutomation _snapshot = CompiledAutomation.Empty;
    private int _appliedConfigurationVersion = -1;
    private bool _hasConfigurationError;

    public AutomationEngine(Automation automation, AutomationInputQueue queue, AutomationStateKeeper keeper, ILogger logger)
    {
        var services = automation.Services;
        _automation = automation;
        _keeper = keeper;
        _logger = logger;
        _triggers = new AutomationTriggers(services, queue, logger);
        _dispatcher = new RunDispatcher(this, services, keeper, logger);
        _states = new StateTransitions(automation, keeper, services.TimeProvider, logger);
        _status = new AutomationStatusReporter(automation, logger);
    }

    public string Name => _automation.Title ?? "Automation";

    IInterceptorSubject IRuleRunHost.Automation => _automation;

    public string? CurrentState => _automation.CurrentState;

    public bool IsWatched(PropertyReference property) => _triggers.IsWatched(property);

    /// <summary>Restores the checkpoint without a state-change event and applies the configuration.</summary>
    public void Start()
    {
        var checkpoint = _keeper.Load(out var error);
        if (error is not null)
        {
            _status.Fail($"The checkpoint could not be read, so the automation starts from its initial state: {error}");
        }
        else if (checkpoint is null)
        {
            _logger.LogInformation("The automation {Automation} has no checkpoint at {Path} and starts from its initial state.", Name, _keeper.Store.FilePath);
        }

        _automation.CurrentState = checkpoint?.CurrentState;
        _automation.StateEnteredAt = checkpoint?.StateEnteredAt;
        ApplyConfigurationIfChanged();
        if (_automation.Status == ServiceStatus.Starting)
        {
            _status.SetRunning();
        }
    }

    public void ApplyConfigurationIfChanged()
    {
        var version = _automation.ConfigurationVersion;
        if (version == _appliedConfigurationVersion)
        {
            return;
        }

        _appliedConfigurationVersion = version;
        var rules = _automation.Rules;
        _keeper.RestoreRules(rules);
        if (AutomationCompiler.TryCompile(_automation.States, _automation.InitialState, rules, _automation.RuleOrder, out var compiled, out var error))
        {
            Replace(compiled);
        }
        else
        {
            KeepPrevious(rules, error);
        }
    }

    public Task OnPropertyChangedAsync(PropertyChangedInput input, CancellationToken cancellationToken) =>
        DispatchAsync(_triggers.OnPropertyChanged(_snapshot.Rules, input), cancellationToken);

    public Task OnTickAsync(CancellationToken cancellationToken) =>
        DispatchAsync(_triggers.OnTick(_snapshot.Rules), cancellationToken);

    public Task OnEventAsync(EventInput input, CancellationToken cancellationToken) =>
        DispatchAsync(_triggers.OnEvent(input), cancellationToken);

    public Task OnGraphChangedAsync(CancellationToken cancellationToken) =>
        DispatchAsync(_triggers.OnGraphChanged(_snapshot.Rules), cancellationToken);

    public Task OnActivationAsync(ActivationInput input, CancellationToken cancellationToken)
    {
        var rules = input.Rules.Where(rule => rule.IsActivated && _snapshot.Rules.Contains(rule)).ToList();
        return DispatchAsync(_triggers.OnActivation(rules, _snapshot.Rules), cancellationToken);
    }

    public Task ReportFaultsAsync(QueueFaults faults, CancellationToken cancellationToken)
    {
        if (faults.FeedbackChains > 0)
        {
            _status.Fail($"A feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped: {faults.FeedbackChains} inputs were dropped.");
        }

        if (faults.Overloaded == 0)
        {
            return Task.CompletedTask;
        }

        _status.Fail($"The input queue overflowed: {faults.Overloaded} inputs were dropped and the rules were evaluated again.");
        return OnGraphChangedAsync(cancellationToken);
    }

    public void ReportUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "The automation {Automation} failed to process an input.", Name);
        _status.SetError(exception.Message);
    }

    public void RefreshRuleStatuses() =>
        RuleStatusPublisher.Publish(_automation.Rules, _snapshot, GetInactiveReason, _automation.Services.TimeProvider.LocalTimeZone);

    public void Stop()
    {
        foreach (var rule in _snapshot.Rules)
        {
            Deactivate(rule);
        }
    }

    public string? ChangeState(string targetState) => _states.ChangeState(_snapshot, targetState);

    private void Replace(CompiledAutomation compiled)
    {
        CarryOver(_snapshot, compiled);
        foreach (var rule in _snapshot.Rules)
        {
            Deactivate(rule);
        }

        _snapshot = compiled;
        PruneCheckpoint();
        var problem = _states.EnterInitialState(compiled) ?? StateTransitions.CheckState(compiled, CurrentState);
        if (problem is not null)
        {
            _status.Fail(problem);
        }
        else if (_hasConfigurationError)
        {
            _status.SetRunning();
        }

        _hasConfigurationError = false;
        _status.ApplyEnabled();
        ActivateRules();
    }

    private void KeepPrevious(IReadOnlyDictionary<string, AutomationRule> rules, string error)
    {
        var kept = new List<CompiledRule>();
        foreach (var rule in _snapshot.Rules)
        {
            if (rules.TryGetValue(rule.Key, out var subject))
            {
                rule.Rule = subject;
                kept.Add(rule);
            }
            else
            {
                Deactivate(rule);
            }
        }

        _snapshot = _snapshot.WithRules(kept);
        _hasConfigurationError = true;
        _status.Fail($"The configuration is invalid and was not applied; the previous one keeps running: {error}");

        // The rule instances changed, so the relative paths of the kept rules resolve again.
        _triggers.Rebind(_snapshot.Rules);
    }

    // A trigger that stays the same in a rule that stays active keeps its state, so that a Save does not run it again.
    private void CarryOver(CompiledAutomation previous, CompiledAutomation next)
    {
        foreach (var rule in next.Rules)
        {
            if (previous.FindRule(rule.Key) is { IsActivated: true } old && IsActive(rule))
            {
                AutomationTriggers.CarryOver(old, rule);
            }
        }
    }

    private void ActivateRules()
    {
        var pending = new List<CompiledRule>();
        foreach (var rule in _snapshot.Rules)
        {
            if (rule.IsActivated || !IsActive(rule))
            {
                continue;
            }

            rule.IsActivated = true;
            pending.Add(rule);
        }

        _triggers.Activate(pending, _snapshot.Rules);
    }

    private void ApplyScopeChange()
    {
        foreach (var rule in _snapshot.Rules)
        {
            if (rule.IsActivated && !IsActive(rule))
            {
                Deactivate(rule);
            }
        }

        ActivateRules();
    }

    private static void Deactivate(CompiledRule rule)
    {
        rule.IsActivated = false;
        AutomationTriggers.Deactivate(rule);
    }

    private void PruneCheckpoint()
    {
        if (_keeper.Persisted.Rules.Keys.All(key => _snapshot.FindRule(key) is not null))
        {
            return;
        }

        var pruned = AutomationStateKeeper.WithRulesOf(_keeper.Persisted, _snapshot.Rules.Select(rule => rule.Key));
        if (_keeper.TryPersist(pruned) is { } error)
        {
            _logger.LogWarning("The automation {Automation} could not remove deleted rules from its checkpoint: {Error}", Name, error);
        }
    }

    private async Task DispatchAsync(IReadOnlyList<RunRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return;
        }

        var summary = await _dispatcher.DispatchAsync(requests, IsActive, cancellationToken);
        if (summary.ChangedState)
        {
            ApplyScopeChange();
        }

        _status.Apply(summary, _hasConfigurationError);
    }

    private bool IsActive(CompiledRule rule) => rule.IsActiveIn(_automation.Enabled, CurrentState);

    private string? GetInactiveReason(CompiledRule rule)
    {
        if (!_automation.Enabled)
        {
            return "Inactive: the automation is disabled";
        }

        return rule.InState is { } state && !string.Equals(state, CurrentState, StringComparison.Ordinal)
            ? $"Inactive outside state {state}"
            : null;
    }
}
