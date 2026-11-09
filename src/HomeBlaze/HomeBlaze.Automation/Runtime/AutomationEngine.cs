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
    private readonly AutomationInputQueue _queue;
    private readonly ILogger _logger;
    private CompiledAutomation _snapshot = CompiledAutomation.Empty;
    private IReadOnlyList<string> _roles = [];
    private IReadOnlyList<string>? _rejectedRoles;
    private int _appliedConfigurationVersion = -1;
    private bool _hasConfigurationError;

    // An unreadable checkpoint stays shown after successful runs, until a reset or a valid configuration is applied.
    private bool _hasCheckpointError;
    // Set by a stop under the lock of _status, which the worker holds while it writes the automation and its rules, so
    // that an abandoned worker writes nothing after the stop showed the rules as stopped.
    private bool _isAbandoned;

    // A saved state that the configuration no longer has keeps every rule inactive until a reset enters the initial state.
    private bool _hasStateError;
    private string? _runWriteError;
    private readonly RuleStatusPublisher _statusPublisher = new();

    // Created once, since the worker uses them after every input.
    private readonly Action _publishRuleStatuses;
    private readonly Func<CompiledRule, string?> _getInactiveReason;
    private readonly Func<CompiledRule, bool> _isActive;

    /// <summary>
    /// Creates the engine, whose property triggers publish their watched set to <paramref name="changes"/> and whose
    /// triggers store the graph routes of their resolutions in <paramref name="routes"/>.
    /// </summary>
    public AutomationEngine(
        Automation automation, AutomationInputQueue queue, AutomationStateKeeper keeper, ILogger logger, AutomationChangeReceiver changes, GraphRoutes routes)
    {
        _automation = automation;
        _keeper = keeper;
        _queue = queue;
        _logger = logger;
        _triggers = AutomationTriggers.For(automation, queue, logger, () => Name, changes.PublishWatched, routes);
        _dispatcher = new RunDispatcher(this, automation, keeper, logger);
        _states = new StateTransitions(automation, keeper, automation.TimeProvider, logger);
        _status = new AutomationStatusReporter(automation, logger);
        _publishRuleStatuses = PublishRuleStatuses;
        _getInactiveReason = GetInactiveReason;
        _isActive = IsActive;
    }

    public string Name => _automation.Title;

    IInterceptorSubject IRuleRunHost.Automation => _automation;

    public string? CurrentState => _automation.CurrentState;

    public IReadOnlyList<string> Roles => _roles;

    public IReadOnlyList<string>? RejectedRoles => _rejectedRoles;

    // White-box test hook: the compiled rules applied last.
    internal IReadOnlyList<CompiledRule> Rules => _snapshot.Rules;

    public bool RunUnlessAbandoned(Action write)
    {
        lock (_status)
        {
            if (_isAbandoned)
            {
                return false;
            }

            write();
            return true;
        }
    }

    /// <summary>Restores the checkpoint without a state-change event and applies the configuration.</summary>
    public void Start()
    {
        _hasCheckpointError = false;
        var checkpoint = _keeper.Load(_automation.TimeProvider.GetUtcNow(), out var error);
        if (error is null && checkpoint is null)
        {
            _logger.LogInformation("The automation {Automation} has no checkpoint at {Path} and starts from its initial state.", Name, _keeper.Store.FilePath);
        }

        Begin(checkpoint);
        if (error is not null)
        {
            _hasCheckpointError = true;
            _status.Fail(error);
        }
        else if (_automation.Status == ServiceStatus.Starting)
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
        _hasCheckpointError = false;
        _runWriteError = null;

        // Written at once, so that an older file at the new path is not read at the next start.
        if (_keeper.TryPersist(_keeper.Current) is { } error)
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

        // Read after the version, so that the rules that the configuration subscribes handle the events received after it was requested.
        var requestedAt = _queue.ConfigurationRequestedAt;
        _appliedConfigurationVersion = version;
        var rules = _automation.Rules;
        var roles = _automation.Roles;
        TakeRunsOfReplacedRules(rules);
        _keeper.RestoreRules(rules);
        if (AutomationCompiler.TryCompile(_automation, roles, rules, out var compiled, out var error))
        {
            _roles = [.. roles];
            _rejectedRoles = null;
            Replace(compiled, requestedAt);
        }
        else
        {
            // The kept rules run only what both role sets allow, so that roles narrowed in a rejected file still apply.
            _rejectedRoles = roles.All(_roles.Contains) && _roles.All(roles.Contains) ? null : [.. roles];
            KeepPrevious(rules, error);
        }
    }

    /// <summary>
    /// Gets when the next tick is due: for a trigger or an Only if that is due, to write the runs or to log drops; null when nothing is,
    /// so that an automation without holds, clock reads, waiting rules or unwritten runs is never woken up.
    /// </summary>
    public DateTimeOffset? GetNextDueAt(DateTimeOffset now)
    {
        return AutomationTriggers.Earliest(AutomationTriggers.Earliest(_triggers.GetNextDueAt(_snapshot.Rules, now), _keeper.RunsDueAt), _status.UnloggedFaultsDueAt);
    }

    /// <summary>
    /// Runs the requests of an input of the triggers: a property change, a tick, which also writes the runs that are due,
    /// an event, a graph change, an activation or a recheck of the pending triggers.
    /// </summary>
    public Task HandleAsync(AutomationInput input, CancellationToken cancellationToken)
    {
        if (input is TickInput)
        {
            WriteRunsIfDue();
            _status.LogUnloggedFaults(_automation.TimeProvider.GetUtcNow());
        }

        return DispatchAsync(_triggers.GetRequests(input, _snapshot.Rules), cancellationToken);
    }

    // A failed write of the runs is shown and logged when its error changes, stays shown while the writes fail, and is
    // cleared once the runs are written.
    private void WriteRunsIfDue()
    {
        var error = _keeper.WriteRunsIfDue(_automation.TimeProvider.GetUtcNow());
        if (error is not null)
        {
            if (!string.Equals(error, _runWriteError, StringComparison.Ordinal))
            {
                _runWriteError = error;
                _status.Fail(DescribeRunWriteError(error));
            }

            return;
        }

        if (_runWriteError is not null && !_keeper.HasUnwrittenRuns)
        {
            _runWriteError = null;
            _logger.LogInformation("The automation {Automation} saved its runs again.", Name);
            if (!HasStickyError)
            {
                _status.SetRunning();
            }
        }
    }

    private static string DescribeRunWriteError(string error) =>
        $"The runs could not be saved and are saved again in {AutomationStateKeeper.RunWriteDelay.TotalSeconds:0} seconds: {error}";

    /// <summary>
    /// Enters the initial state, clears the trigger times and starts every active rule over as if it entered its state:
    /// fresh holds and the activation policy of its triggers applies again, also when the state did not change. The events
    /// queued before the reset were handled before it, and those queued after it are handled by the rules active after it.
    /// A saved reset clears the error shown unless the configuration is invalid, since a reset is how an automation leaves
    /// a saved state that its configuration no longer has.
    /// </summary>
    public void ResetState()
    {
        _keeper.AllowOverwrite();
        if (_states.Reset(_snapshot, _automation.Rules.Values, RestartRules) is { } error)
        {
            _status.Fail(error);
            return;
        }

        _hasCheckpointError = false;
        _runWriteError = null;
        if (!_hasConfigurationError)
        {
            _status.SetRunning();
        }
    }

    private void RestartRules()
    {
        _hasStateError = false;
        foreach (var rule in _snapshot.Rules)
        {
            Deactivate(rule);
        }

        ActivateRules(_queue.LastEventSequence);
    }

    /// <summary>
    /// Reports the dropped inputs and makes up for them, after the inputs that were queued before the drop. The runs that
    /// inputs dropped from a feedback chain request do not run, because running them would continue the chain. After an
    /// overload every trigger is evaluated again.
    /// </summary>
    public async Task ResynchronizeAsync(DroppedInputs dropped, CancellationToken cancellationToken)
    {
        _status.ReportFaults(dropped.Faults, _automation.TimeProvider.GetUtcNow());
        var stopped = _triggers.MakeUpForChain(_snapshot.Rules, dropped);
        if (stopped.Count > 0)
        {
            _dispatcher.Skip(stopped, $"a feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped");

            // No recheck of their Only if follows, since a recheck would run them and continue the chain.
            _triggers.TrackPending(_snapshot.Rules, stopped);
        }

        if (dropped.HasOverload)
        {
            await DispatchAsync(_triggers.MakeUpForOverload(_snapshot.Rules, dropped), cancellationToken);
        }
    }

    public void ReportUnexpectedError(Exception exception)
    {
        _logger.LogError(exception, "The automation {Automation} failed to process an input.", Name);
        RunUnlessAbandoned(() => _status.SetError(exception.Message));
    }

    /// <summary>
    /// Shows every rule as stopped and has a worker that a stop abandoned change nothing anymore. Called by the stop, not
    /// by the worker.
    /// </summary>
    public void Abandon()
    {
        lock (_status)
        {
            _isAbandoned = true;
            RuleStatusPublisher.PublishStopped(_automation.Rules, _automation.TimeProvider.LocalTimeZone);
        }
    }

    public void RefreshRuleStatuses() => RunUnlessAbandoned(_publishRuleStatuses);

    private void PublishRuleStatuses()
    {
        var notApplied = _hasConfigurationError && _automation.ConfigurationVersion == _appliedConfigurationVersion
            ? RuleStatusPublisher.NotAppliedInvalid
            : RuleStatusPublisher.NotAppliedYet;
        _statusPublisher.Publish(_automation, _snapshot, _getInactiveReason, notApplied);
    }

    private void Stop()
    {
        foreach (var rule in _snapshot.Rules)
        {
            Deactivate(rule);
            _triggers.StopListening(rule);
        }
    }

    /// <summary>Deactivates every rule, writes the runs not written yet and shows every rule as inactive, once the worker ends.</summary>
    public void End()
    {
        Stop();
        RunUnlessAbandoned(() =>
        {
            if (_keeper.WriteRuns() is { } error)
            {
                _logger.LogWarning("The automation {Automation} could not save its last runs when it stopped: {Error}", Name, error);
            }

            RuleStatusPublisher.PublishStopped(_automation.Rules, _automation.TimeProvider.LocalTimeZone);
        });
    }

    public string? ChangeState(string targetState) => _states.ChangeState(_snapshot, targetState);

    private void Unload()
    {
        Stop();
        _snapshot = CompiledAutomation.Empty;
        _roles = [];
        _rejectedRoles = null;
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

    private void Replace(CompiledAutomation compiled, long requestedAt)
    {
        CarryOver(_snapshot, compiled);
        Stop();
        _snapshot = compiled;
        PruneCheckpoint();

        // An unreadable checkpoint that could not be moved aside stays an error until a reset, since nothing is written.
        var refusesWrites = _keeper.RefusesWrites;
        var stateProblem = StateTransitions.CheckState(compiled, CurrentState);
        var hadStateError = _hasStateError;
        _hasStateError = stateProblem is not null;
        var problem = _states.EnterInitialState(compiled) ?? stateProblem;
        if (problem is not null)
        {
            _status.Fail(problem);
        }
        else if ((_hasConfigurationError || _hasCheckpointError || hadStateError) && !refusesWrites)
        {
            // The runs that still cannot be written are shown again in place of the error that this clears.
            if (_runWriteError is not null)
            {
                _status.SetError(DescribeRunWriteError(_runWriteError));
            }
            else
            {
                _status.SetRunning();
            }
        }

        _hasConfigurationError = false;
        _hasCheckpointError = refusesWrites;
        _status.ApplyEnabled();
        ActivateRules(requestedAt);

        // A trigger that waits keeps waiting across a Save, whose new Only if may let it run now.
        if (_triggers.HasPending)
        {
            _triggers.RequestPendingRecheck();
        }
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
                _triggers.StopListening(rule);
            }
        }

        _snapshot = _snapshot.WithRules(kept);
        _hasConfigurationError = true;
        _status.Fail($"The configuration is invalid and was not applied; the previous one keeps running: {error}");

        // The rule instances changed, so the relative paths of the kept rules resolve again.
        _triggers.Rebind(_snapshot.Rules);
        _triggers.RefreshActiveSubscriptions(_snapshot.Rules);
    }

    // A property trigger that stays the same in a rule that stays active keeps its state, so that a Save does not run it
    // again, and an event trigger that stays the same in a rule that keeps listening keeps its subscription, so that a
    // Save drops no queued event.
    private void CarryOver(CompiledAutomation previous, CompiledAutomation next)
    {
        foreach (var rule in next.Rules)
        {
            if (previous.FindRule(rule.Key) is not { } old)
            {
                continue;
            }

            if (old.IsActivated && IsActive(rule))
            {
                AutomationTriggers.CarryOverStates(old, rule);
            }

            if (ListensForEvents(rule))
            {
                AutomationTriggers.CarryOverSubscriptions(old, rule);
            }
        }
    }

    // Has the rules that are not Off listen for the events after joinedAfter and activates the rules of the current
    // state. Returns whether a pending property trigger began to be tracked.
    private bool ActivateRules(long joinedAfter)
    {
        var pending = new List<CompiledRule>();
        foreach (var rule in _snapshot.Rules)
        {
            if (ListensForEvents(rule))
            {
                _triggers.Listen(rule, joinedAfter);
            }
            else
            {
                _triggers.StopListening(rule);
            }

            if (rule.IsActivated || !IsActive(rule))
            {
                continue;
            }

            rule.IsActivated = true;
            pending.Add(rule);
        }

        _triggers.RefreshActiveSubscriptions(_snapshot.Rules);
        return _triggers.Activate(pending, _snapshot.Rules);
    }

    // Called after a state change, which left the state of every activated rule of a state, so such a rule starts over
    // like a rule that entered its state, also when a later change of the same input entered its state again.
    private bool ApplyScopeChange()
    {
        foreach (var rule in _snapshot.Rules)
        {
            if (rule.IsActivated && (rule.InState is not null || !IsActive(rule)))
            {
                Deactivate(rule);
            }
        }

        return ActivateRules(_queue.LastEventSequence);
    }

    private static void Deactivate(CompiledRule rule)
    {
        rule.IsActivated = false;
        rule.SkipReason = null;
        AutomationTriggers.Deactivate(rule);
    }

    private void PruneCheckpoint()
    {
        if (_keeper.Current.Rules.Keys.All(key => _snapshot.FindRule(key) is not null))
        {
            return;
        }

        var pruned = AutomationStateKeeper.WithRulesOf(_keeper.Current, _snapshot.Rules.Select(rule => rule.Key));
        if (_keeper.TryPersist(pruned) is { } error)
        {
            _logger.LogWarning("The automation {Automation} could not remove deleted rules from its checkpoint: {Error}", Name, error);
        }
    }

    private async Task DispatchAsync(IReadOnlyList<RunRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            _triggers.TrackPending(_snapshot.Rules, requests);
            return;
        }

        DispatchSummary summary;
        var hasNewPending = false;
        try
        {
            summary = await _dispatcher.DispatchAsync(requests, _isActive, cancellationToken);
        }
        finally
        {
            RunUnlessAbandoned(() =>
            {
                // The activation of the entered state may track a trigger that became pending, which TrackPending then
                // finds tracked already.
                var tracksNewPending = PublishStateChanges();
                hasNewPending = _triggers.TrackPending(_snapshot.Rules, requests) || tracksNewPending;
            });
        }

        RunUnlessAbandoned(() =>
        {
            // A skipped trigger of a rule that stays active runs in a later input, after the state change, and a trigger
            // that started to wait checks Only if again once the properties of Only if are watched.
            if (hasNewPending || summary.SkippedForStateChange)
            {
                _triggers.RequestPendingRecheck();
            }

            _status.Apply(summary, HasStickyError);
        });
    }

    // The rules of the entered state are activated first, so that their activation is queued before the events of
    // entering it. Returns whether a pending property trigger began to be tracked.
    private bool PublishStateChanges()
    {
        if (!_states.HasPendingChanges)
        {
            return false;
        }

        var tracksNewPending = ApplyScopeChange();
        _states.PublishPendingChanges();
        return tracksNewPending;
    }

    private bool HasStickyError => _hasConfigurationError || _hasCheckpointError || _hasStateError || _runWriteError is not null;

    private bool IsActive(CompiledRule rule) => !_hasStateError && rule.IsActiveIn(_automation.Enabled, CurrentState);

    // In every state, so that an event queued while a rule of another state ran is handled by the rules of the state
    // that is current when the worker takes the event.
    private bool ListensForEvents(CompiledRule rule) => !_hasStateError && _automation.Enabled && rule.Mode != AutomationRuleMode.Off;

    private string? GetInactiveReason(CompiledRule rule)
    {
        if (!_automation.Enabled)
        {
            return "Inactive: the automation is disabled";
        }

        if (_hasStateError)
        {
            return "Inactive until Reset state";
        }

        return rule.InState is { } state && !string.Equals(state, CurrentState, StringComparison.Ordinal)
            ? $"Inactive outside state {state}"
            : null;
    }
}
