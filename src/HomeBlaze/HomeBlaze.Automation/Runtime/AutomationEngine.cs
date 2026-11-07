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

    /// <summary>Creates the engine, whose property triggers publish their watched set to <paramref name="changes"/>.</summary>
    public AutomationEngine(Automation automation, AutomationInputQueue queue, AutomationStateKeeper keeper, ILogger logger, AutomationChangeReceiver changes)
    {
        var services = automation.Services;
        _automation = automation;
        _keeper = keeper;
        _logger = logger;
        _triggers = new AutomationTriggers(services, queue, logger, () => Name, changes.PublishWatched);
        _dispatcher = new RunDispatcher(this, services, keeper, logger);
        _states = new StateTransitions(automation, keeper, services.TimeProvider, logger);
        _status = new AutomationStatusReporter(automation, logger);
    }

    public string Name => _automation.Title ?? "Automation";

    IInterceptorSubject IRuleRunHost.Automation => _automation;

    public string? CurrentState => _automation.CurrentState;

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

        Begin(checkpoint);
        if (_automation.Status == ServiceStatus.Starting)
        {
            _status.SetRunning();
        }
    }

    /// <summary>
    /// Starts again as a new automation, after the keeper was given the store of the path the automation moved to: the
    /// initial state, no trigger times, no runs, no trigger state carried over, and no error about the runs it no longer
    /// has. An invalid configuration or a checkpoint that cannot be written is shown again.
    /// </summary>
    public void StartFresh()
    {
        Unload();

        // Written at once, so that an older file at the new path is not read at the next start.
        if (_keeper.TryPersist(_keeper.Persisted) is { } error)
        {
            _status.Fail($"The checkpoint at the new path could not be written: {error}");
        }
        else
        {
            _status.SetRunning();
        }

        _logger.LogInformation("The automation {Automation} moved and starts from its initial state at {Path}.", Name, _keeper.Store.FilePath);
        Begin(null);
    }

    /// <summary>
    /// Starts again from the checkpoint in the store the keeper was given, as at a start, for an automation that started
    /// before it had a path and so had no checkpoint until now.
    /// </summary>
    public void StartFromCheckpoint()
    {
        Unload();
        Start();
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
        TakeRunsOfReplacedRules(rules);
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

    /// <summary>
    /// Enters the initial state, clears the trigger times and starts every active rule over as if it entered its state:
    /// fresh holds, the activation policy of its triggers applies again and its event triggers subscribe again, also when the
    /// state did not change. A saved reset clears the error shown unless the configuration is invalid, since a reset is how
    /// an automation leaves a saved state that its configuration no longer has.
    /// </summary>
    public Task ResetStateAsync()
    {
        if (_states.Reset(_snapshot, _automation.Rules.Values, RestartRules) is { } error)
        {
            _status.Fail(error);
            return Task.CompletedTask;
        }

        if (!_hasConfigurationError)
        {
            _status.SetRunning();
        }

        return Task.CompletedTask;
    }

    private void RestartRules()
    {
        foreach (var rule in _snapshot.Rules)
        {
            if (rule.IsActivated)
            {
                Deactivate(rule);
            }
        }

        ApplyScopeChange();
    }

    /// <summary>
    /// Reports the dropped inputs and makes up for them, after the inputs that were queued before the drop. The runs that
    /// inputs dropped from a feedback chain request are recorded as canceled, because running them would continue the
    /// chain. After an overload every trigger is evaluated again.
    /// </summary>
    public async Task ResynchronizeAsync(DroppedInputs dropped, CancellationToken cancellationToken)
    {
        _status.ReportFaults(dropped);
        var canceled = _triggers.MakeUpForChain(_snapshot.Rules, dropped);
        if (canceled.Count > 0)
        {
            var reason = $"Canceled: a feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped";
            _status.Apply(_dispatcher.Cancel(canceled, reason), _hasConfigurationError);
        }

        if (dropped.HasOverload)
        {
            await DispatchAsync(_triggers.MakeUpForOverload(_snapshot.Rules, dropped), cancellationToken);
        }
    }

    public void ReportUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "The automation {Automation} failed to process an input.", Name);
        _status.SetError(exception.Message);
    }

    public void RefreshRuleStatuses()
    {
        var notApplied = _hasConfigurationError && _automation.ConfigurationVersion == _appliedConfigurationVersion
            ? RuleStatusPublisher.NotAppliedInvalid
            : RuleStatusPublisher.NotAppliedYet;
        RuleStatusPublisher.Publish(_automation.Rules, _snapshot, GetInactiveReason, _automation.Services.TimeProvider.LocalTimeZone, notApplied);
    }

    public void Stop()
    {
        foreach (var rule in _snapshot.Rules)
        {
            Deactivate(rule);
        }
    }

    /// <summary>Deactivates every rule and shows it as inactive, once the worker ends.</summary>
    public void End()
    {
        Stop();
        RuleStatusPublisher.PublishStopped(_automation.Rules, _automation.Services.TimeProvider.LocalTimeZone);
    }

    public string? ChangeState(string targetState) => _states.ChangeState(_snapshot, targetState);

    private void Unload()
    {
        Stop();
        _snapshot = CompiledAutomation.Empty;
        _appliedConfigurationVersion = -1;
    }

    private void Begin(AutomationCheckpoint? checkpoint)
    {
        _automation.CurrentState = checkpoint?.CurrentState;
        _automation.StateEnteredAt = checkpoint?.StateEnteredAt;
        _keeper.RestoreAllRules(_automation.Rules);
        ApplyConfigurationIfChanged();
    }

    // Runs are recorded on the applied instances until now, so the copy made when the rules were set may miss the last
    // ones; this one comes after every run and is the one that counts.
    private void TakeRunsOfReplacedRules(IReadOnlyDictionary<string, AutomationRule> rules)
    {
        foreach (var rule in _snapshot.Rules)
        {
            if (rules.TryGetValue(rule.Key, out var subject) && !ReferenceEquals(subject, rule.Rule))
            {
                subject.CopyRuntimeStateFrom(rule.Rule);
            }
        }
    }

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

        DispatchSummary summary;
        try
        {
            summary = await _dispatcher.DispatchAsync(requests, IsActive, cancellationToken);
        }
        finally
        {
            PublishStateChanges();
        }

        _status.Apply(summary, _hasConfigurationError);
    }

    // The rules of the entered state are activated first, so that they receive the event of entering it.
    private void PublishStateChanges()
    {
        if (!_states.HasPendingChanges)
        {
            return;
        }

        ApplyScopeChange();
        _states.PublishPendingChanges();
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
