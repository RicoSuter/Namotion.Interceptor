using System.ComponentModel;
using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Automation;

/// <summary>
/// Rules that react to property changes and subject events, optionally within named states. Every input and every run is
/// processed on one worker per automation. The current state, the time it was entered and the last run of each rule are
/// kept in a checkpoint file below the data directory, not in the configuration.
/// </summary>
[Category("Automation")]
[Description("Runs rules on property changes and events, optionally within named states.")]
[DisplayName("Automation")]
[SubjectIcon("AutoMode")]
[InterceptorSubject]
public partial class Automation : BackgroundService, IConfigurable, IMonitoredService, ITitleProvider, IIconProvider,
    IObservable<AutomationStateChangedEvent>, ILifecycleHandler
{
    private readonly ILogger<Automation> _logger;
    private readonly Subject<AutomationStateChangedEvent> _stateChanged = new();
    private AutomationRuntime? _runtime;
    private int _configurationVersion;
    private volatile bool _isDetached;

    /// <summary>Creates an automation with the services it takes, which dependency injection resolves.</summary>
    /// <param name="pathResolver">Resolves the paths of the rules.</param>
    /// <param name="timeProvider">
    /// The clock of <c>now</c>, <c>time</c>, holds and timeouts; its local time zone is that of <c>time</c>. The system clock
    /// keeps the time zone that the process read first, so a change of the time zone of the host applies after a restart.
    /// </param>
    /// <param name="authorizer">Decides which operation calls and property writes the rules may make.</param>
    /// <param name="serviceProvider">Resolves the services that operations take with <c>[FromServices]</c>.</param>
    /// <param name="logger">The logger of the automation.</param>
    public Automation(
        SubjectPathResolver pathResolver,
        TimeProvider timeProvider,
        IAutomationAuthorizer authorizer,
        IServiceProvider serviceProvider,
        ILogger<Automation> logger)
    {
        PathResolver = pathResolver;
        TimeProvider = timeProvider;
        Authorizer = authorizer;
        ServiceProvider = serviceProvider;
        ChangeDispatcher = AutomationChangeDispatcher.From(serviceProvider);
        _logger = logger;
        Enabled = true;
        Roles = [];
        States = [];
        RuleOrder = [];
        Rules = new Dictionary<string, AutomationRule>();
        Status = ServiceStatus.Stopped;
    }

    [Configuration]
    public partial string? Name { get; set; }

    /// <summary>Whether the rules are active. A disabled automation keeps its state and its checkpoint.</summary>
    [Configuration]
    public partial bool Enabled { get; set; }

    /// <summary>
    /// The names of the roles that decide which operations the rules may call and which properties they may write. What the rules may call and
    /// write for these roles is decided by the registered <see cref="IAutomationAuthorizer"/>; a state change needs no role. Replaced as a whole, never changed in place.
    /// </summary>
    [Configuration]
    public partial string[] Roles { get; set; }

    /// <summary>The names of the states, or none for an automation without named states.</summary>
    [Configuration]
    public partial string[] States { get; set; }

    /// <summary>The state of a new automation, and the state that <c>ResetState</c> enters; one of <see cref="States"/>.</summary>
    [Configuration]
    public partial string? InitialState { get; set; }

    /// <summary>The keys of the rules in the order they process an input; rules not listed follow in key order.</summary>
    [Configuration]
    public partial string[] RuleOrder { get; set; }

    /// <summary>The rules by a stable key that is not their name, so that their paths survive renaming.</summary>
    [Configuration]
    public partial Dictionary<string, AutomationRule> Rules { get; set; }

    [State("Current State", IsDiscrete = true, Position = 1)]
    public partial string? CurrentState { get; internal set; }

    [State("State Entered", Position = 2)]
    public partial DateTimeOffset? StateEnteredAt { get; internal set; }

    [State(IsDiscrete = true, Position = 3)]
    public partial ServiceStatus Status { get; internal set; }

    [State(Position = 4)]
    public partial string? StatusMessage { get; internal set; }

    /// <summary>
    /// Whether the worker runs the rules: while the automation is running, or shows an error while its worker runs, such as
    /// after a failed action. Not while it is starting, stopping or stopped, nor after its worker ended with an error.
    /// </summary>
    public bool IsRunningRules => Volatile.Read(ref _runtime) is not null && Status is ServiceStatus.Running or ServiceStatus.Error;

    [Derived]
    public string Title => Name ?? "Automation";

    public string? IconName => "AutoMode";

    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running => "Success",
        ServiceStatus.Error => "Error",
        _ => null
    };

    internal SubjectPathResolver PathResolver { get; }

    /// <summary>The clock of <c>now</c>, <c>time</c>, holds and timeouts; its local time zone is that of <c>time</c>.</summary>
    internal TimeProvider TimeProvider { get; }

    internal IAutomationAuthorizer Authorizer { get; }

    /// <summary>Resolves the services that operations take with <c>[FromServices]</c>.</summary>
    internal IServiceProvider ServiceProvider { get; }

    /// <summary>Routes the property changes to the automations: the registered dispatcher, or the one that every automation without one shares.</summary>
    internal AutomationChangeDispatcher ChangeDispatcher { get; }

    internal int ConfigurationVersion => Volatile.Read(ref _configurationVersion);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        var runtime = Volatile.Read(ref _runtime);
        runtime?.MarkConfigurationRequested();
        Interlocked.Increment(ref _configurationVersion);
        runtime?.RequestConfiguration();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes to the <see cref="AutomationStateChangedEvent"/> published when the automation enters another state.
    /// Observers are called on the worker of the automation, which processes no other input meanwhile; an exception they
    /// throw is logged and changes nothing in the automation.
    /// </summary>
    public IDisposable Subscribe(IObserver<AutomationStateChangedEvent> observer) => _stateChanged.Subscribe(observer);

    /// <summary>
    /// Enters the initial state, clears the last trigger times of the rules and starts every rule that is active in it over,
    /// as if it entered its state: its triggers take a fresh baseline or evaluate at once according to their activation policy
    /// and running holds restart. The reset is queued like an input: the events received before it are handled before it,
    /// and those received after it by the rules active after it. A state change is published only when the state changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The automation is not running, or the reset could not be queued.</exception>
    [Operation(Title = "Reset State", Description = "Enters the initial state, clears the last trigger times and starts every rule over.", Icon = "RestartAlt", Position = 1)]
    public void ResetState()
    {
        var runtime = Volatile.Read(ref _runtime) ?? throw new InvalidOperationException("The automation is not running.");
        if (!runtime.RequestReset())
        {
            throw new InvalidOperationException("The reset was not queued, because the input queue is full, a feedback chain was stopped or the automation is stopping.");
        }
    }

    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!ReferenceEquals(change.Subject, this))
        {
            return;
        }

        if (change.IsContextAttach)
        {
            _isDetached = false;
        }
        else if (change.IsContextDetach)
        {
            _isDetached = true;
        }
    }

    /// <summary>The worker of the current start, or null while the automation is stopped.</summary>
    internal AutomationRuntime? Runtime => Volatile.Read(ref _runtime);

    /// <summary>Completes once every input queued so far, and every input those caused, was processed, or the worker stopped.</summary>
    internal Task FlushAsync() => Volatile.Read(ref _runtime)?.FlushAsync() ?? Task.CompletedTask;

    internal void PublishStateChanged(AutomationStateChangedEvent stateChanged) => _stateChanged.OnNext(stateChanged);

    // The runtime exists once the start returned, although the worker runs later on another thread, so that a
    // configuration change or a flush right after the start reaches it.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _runtime, new AutomationRuntime(this, _logger));
        Status = ServiceStatus.Starting;
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the worker and waits until it ended, at most until <paramref name="cancellationToken"/> is canceled or 10 seconds
    /// passed. A worker that did not end, such as one in an operation that ignores its cancellation, is abandoned: it changes
    /// neither the automation nor its checkpoint anymore, also after a new start.
    /// </summary>
    /// <remarks>
    /// The runs that an abandoned worker recorded but had not written yet are lost as after a crash: the next start
    /// restores the runs, run counts and trigger times written before them.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await AutomationRuntime.WaitForStopAsync(token => base.StopAsync(token), TimeProvider, cancellationToken);

        // A worker that ended took its runtime; one that did not end, or never ran because the stop came first, left it.
        if (Interlocked.Exchange(ref _runtime, null) is not { } runtime)
        {
            return;
        }

        if (ExecuteTask is { IsCompleted: false })
        {
            _logger.LogWarning(
                "The automation {Automation} did not stop in time, such as for an operation that ignores its cancellation, and its worker is abandoned.",
                Title);
        }

        runtime.Close();
        OnWorkerEnded(runtime);
        runtime.Abandon();
        Status = ServiceStatus.Stopped;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A stop that came first already took the runtime of this start, which may be the runtime of a later start now.
        if (stoppingToken.IsCancellationRequested || Volatile.Read(ref _runtime) is not { } runtime)
        {
            return;
        }

        string? error = null;
        try
        {
            await runtime.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A requested stop; the automation ends as stopped and keeps its last error message.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The automation {Automation} stopped after an unexpected error.", Title);
            error = exception.Message;
        }

        // A worker that a stop abandoned no longer owns the automation, which may run again with another worker.
        if (Interlocked.CompareExchange(ref _runtime, null, runtime) != runtime)
        {
            return;
        }

        OnWorkerEnded(runtime);
        runtime.Release();
        if (error is not null)
        {
            StatusMessage = error;
        }

        Status = error is null ? ServiceStatus.Stopped : ServiceStatus.Error;
    }

    private void OnWorkerEnded(AutomationRuntime runtime) => runtime.DeleteCheckpointIfOrphaned(_isDetached);

    // A configuration file may hold an explicit null, which the runtime never expects.
    partial void OnStatesChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnRolesChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnRuleOrderChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    // A Save replaces the rule instances; the new ones take the run properties before they are attached, so that the
    // history of a run count never sees it start again. The worker copies them again when it applies the configuration,
    // after the runs recorded on the old instances meanwhile.
    partial void OnRulesChanging(ref Dictionary<string, AutomationRule> newValue, ref bool cancel)
    {
        newValue = WithoutNullRules(newValue);
        if (Rules is not { } current)
        {
            return;
        }

        foreach (var (key, rule) in newValue)
        {
            if (rule.RunCount is null && current.TryGetValue(key, out var previous) && !ReferenceEquals(previous, rule))
            {
                rule.CopyRuntimeStateFrom(previous);
            }
        }
    }

    // An explicit null rule becomes an empty one, so that its key is reported as an invalid rule.
    private static Dictionary<string, AutomationRule> WithoutNullRules(Dictionary<string, AutomationRule>? rules)
    {
        if (rules is null)
        {
            return new Dictionary<string, AutomationRule>();
        }

        if (!rules.ContainsValue(null!))
        {
            return rules;
        }

        return rules.ToDictionary(pair => pair.Key, pair => pair.Value ?? new AutomationRule(), rules.Comparer);
    }
}
