using System.ComponentModel;
using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Automation.Persistence;
using HomeBlaze.Automation.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

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
    IObservable<AutomationStateChangedEvent>
{
    private readonly ILogger<Automation> _logger;
    private readonly Subject<AutomationStateChangedEvent> _stateChanged = new();
    private AutomationRuntime? _runtime;
    private int _configurationVersion;

    public Automation(AutomationServices services, ILogger<Automation> logger)
    {
        Services = services;
        _logger = logger;
        Enabled = true;
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

    [Derived]
    public string? Title => Name ?? "Automation";

    public string? IconName => "AutoMode";

    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running => "Success",
        ServiceStatus.Error => "Error",
        _ => null
    };

    internal AutomationServices Services { get; }

    internal int ConfigurationVersion => Volatile.Read(ref _configurationVersion);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _configurationVersion);
        Volatile.Read(ref _runtime)?.RequestConfiguration();
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(IObserver<AutomationStateChangedEvent> observer) => _stateChanged.Subscribe(observer);

    /// <summary>Completes once every input queued so far, and every input those caused, was processed, or the worker stopped.</summary>
    internal Task FlushAsync() => Volatile.Read(ref _runtime)?.FlushAsync() ?? Task.CompletedTask;

    internal void PublishStateChanged(AutomationStateChangedEvent stateChanged) => _stateChanged.OnNext(stateChanged);

    // The runtime exists once the start returned, although the worker runs later on another thread, so that a
    // configuration change or a flush right after the start reaches it.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _runtime, new AutomationRuntime(this, new AutomationCheckpointStore(GetCheckpointPath()), _logger));
        Status = ServiceStatus.Starting;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // A stop before the worker ran leaves the runtime of the start behind.
        if (Interlocked.Exchange(ref _runtime, null) is { } runtime)
        {
            runtime.Close();
            Status = ServiceStatus.Stopped;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Volatile.Read(ref _runtime) is not { } runtime)
        {
            return;
        }

        var finalStatus = ServiceStatus.Stopped;
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
            StatusMessage = exception.Message;
            finalStatus = ServiceStatus.Error;
        }
        finally
        {
            Interlocked.CompareExchange(ref _runtime, null, runtime);
        }

        Status = finalStatus;
    }

    private string? GetCheckpointPath()
    {
        var dataDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        var filePath = AutomationCheckpointStore.GetFilePath(dataDirectory, Services.PathResolver.GetPath(this, PathStyle.Canonical));
        if (filePath is null)
        {
            _logger.LogWarning("The automation {Automation} has no data directory or path, so its state is not kept across restarts.", Title);
        }

        return filePath;
    }

    // A configuration file may hold an explicit null, which the runtime never expects.
    partial void OnStatesChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnRuleOrderChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    // A Save replaces the rule instances; the new ones take the run properties before they are attached, so that the
    // history of a run count never sees it start again. The worker copies them again when it applies the configuration,
    // after the runs recorded on the old instances meanwhile.
    partial void OnRulesChanging(ref Dictionary<string, AutomationRule> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, AutomationRule>();
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
}
