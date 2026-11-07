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
    private readonly GraphRoutes _routes;
    private readonly RunDispatcher _dispatcher;
    private readonly StateTransitions _states;
    private readonly AutomationStatusReporter _status;
    private readonly ILogger _logger;
    private CompiledAutomation _snapshot = CompiledAutomation.Empty;
    private int _appliedConfigurationVersion = -1;
    private bool _hasConfigurationError;

    // An unreadable checkpoint stays shown after successful runs, until a reset or a valid configuration is applied.
    private bool _hasCheckpointError;
    // Set by a stop under the lock of _status, which the worker holds while it writes the automation and its rules, so
    // that an abandoned worker writes nothing after the stop showed the rules as stopped.
    private bool _isAbandoned;
    private string? _runWriteError;
    private readonly RuleStatusPublisher _statusPublisher = new();

    // Created once, since the worker publishes the rule statuses after every input.
    private readonly Action _publishRuleStatuses;

    /// <summary>Creates the engine, whose triggers publish their watched set and graph route to <paramref name="changes"/>.</summary>
    public AutomationEngine(Automation automation, AutomationInputQueue queue, AutomationStateKeeper keeper, ILogger logger, AutomationChangeReceiver changes)
    {
        _automation = automation;
        _keeper = keeper;
        _logger = logger;
        _routes = GraphRoutes.For(automation, changes);
        _triggers = new AutomationTriggers(_routes, automation.TimeProvider, queue, logger, () => Name, changes.PublishWatched);
        _dispatcher = new RunDispatcher(this, automation, keeper, logger);
        _states = new StateTransitions(automation, keeper, automation.TimeProvider, logger);
        _status = new AutomationStatusReporter(automation, logger);
        _publishRuleStatuses = PublishRuleStatuses;
    }

    public string Name => _automation.Title ?? "Automation";

    // White-box test hooks: the applied rules and the graph routes of their paths.
    internal IReadOnlyList<CompiledRule> Rules => _snapshot.Rules;

    internal GraphRoutes Routes => _routes;

    IInterceptorSubject IRuleRunHost.Automation => _automation;

    public string? CurrentState => _automation.CurrentState;

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

    /// <summary>
    /// Gets when the next tick is due: for a trigger or an Only if that is due, to write the runs or to log drops; null when nothing is,
    /// so that an automation without holds, clock reads, waiting rules or unwritten runs is never woken up.
    /// </summary>
    public DateTimeOffset? GetNextDueAt(DateTimeOffset now)
    {
        return Earliest(Earliest(_triggers.GetNextDueAt(_snapshot.Rules, now), _keeper.RunsDueAt), _status.UnloggedFaultsDueAt);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second) => first is null || second < first ? second : first;

    /// <summary>Takes the properties of the graph changes, when the worker takes a graph change.</summary>
    public void TakeGraphChanges() => _routes.TakeGraphChanges();

    /// <summary>Whether the graph changes taken last may have moved the automation.</summary>
    public bool MayHaveMoved => _routes.NeedsResolving(_routes.Location, _routes.LocationVersion);

    /// <summary>Records the graph route of the checkpoint path, before the worker reads the path.</summary>
    public void RecordLocation() => _routes.RecordLocation(_automation);

    /// <summary>Publishes the graph route of the rules and of the checkpoint path, see <see cref="GraphRoutes.Publish"/>.</summary>
    public void PublishRoutes(int chainDepth) => _routes.Publish(_snapshot.Rules, chainDepth);

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
                _status.Fail($"The runs could not be saved and are saved again in {AutomationStateKeeper.RunWriteDelay.TotalSeconds:0} seconds: {error}");
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

    /// <summary>
    /// Enters the initial state, clears the trigger times and starts every active rule over as if it entered its state:
    /// fresh holds, the activation policy of its triggers applies again and its event triggers subscribe again, also when the
    /// state did not change. A saved reset clears the error shown unless the configuration is invalid, since a reset is how
    /// an automation leaves a saved state that its configuration no longer has.
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
        Stop();
        ActivateRules();
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
        _statusPublisher.Publish(_automation, _snapshot, GetInactiveReason, notApplied);
    }

    private void Stop()
    {
        foreach (var rule in _snapshot.Rules)
        {
            Deactivate(rule);
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
        Stop();
        _snapshot = compiled;
        PruneCheckpoint();

        // An unreadable checkpoint that could not be moved aside stays an error until a reset, since nothing is written.
        var refusesWrites = _keeper.RefusesWrites;
        var problem = _states.EnterInitialState(compiled) ?? StateTransitions.CheckState(compiled, CurrentState);
        if (problem is not null)
        {
            _status.Fail(problem);
        }
        else if ((_hasConfigurationError || _hasCheckpointError) && !refusesWrites)
        {
            _status.SetRunning();
        }

        _hasConfigurationError = false;
        _hasCheckpointError = refusesWrites;
        _status.ApplyEnabled();
        ActivateRules();

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
            }
        }

        _snapshot = _snapshot.WithRules(kept);
        _hasConfigurationError = true;
        _status.Fail($"The configuration is invalid and was not applied; the previous one keeps running: {error}");

        // The rule instances changed, so the relative paths of the kept rules resolve again.
        _triggers.Rebind(_snapshot.Rules);
    }

    // A trigger that stays the same in a rule that stays active keeps its state, so that a Save does not run it again, and
    // its subscription, so that a Save drops no queued event.
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
            summary = await _dispatcher.DispatchAsync(requests, IsActive, cancellationToken);
        }
        finally
        {
            RunUnlessAbandoned(() =>
            {
                PublishStateChanges();
                hasNewPending = _triggers.TrackPending(_snapshot.Rules, requests);
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

    private bool HasStickyError => _hasConfigurationError || _hasCheckpointError || _runWriteError is not null;

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
