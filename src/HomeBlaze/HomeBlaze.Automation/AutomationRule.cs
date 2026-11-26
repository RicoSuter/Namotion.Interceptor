using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Automation;

/// <summary>
/// A rule of an automation: its triggers request runs, Only if decides whether a run executes, and its actions run
/// in order. Its key in <see cref="Automation.Rules"/> is its identity, so its state paths survive renaming.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class AutomationRule : IConfigurable, ITitleProvider
{
    /// <summary>
    /// The longest <see cref="LastRunMessage"/>, which keeps it within what history stores for a text. A longer message is
    /// cut to this length, with "..." as its last three characters.
    /// </summary>
    public const int MaxRunMessageLength = 500;

    public AutomationRule()
    {
        Mode = AutomationRuleMode.DryRun;
        Triggers = [];
        Actions = [];
    }

    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial AutomationRuleMode Mode { get; set; }

    /// <summary>The state of the automation in which the rule is active, or null for every state.</summary>
    [Configuration]
    public partial string? InState { get; set; }

    /// <summary>The triggers, combined with OR: each one that fires requests a run.</summary>
    [Configuration]
    public partial AutomationTrigger[] Triggers { get; set; }

    /// <summary>
    /// The condition a requested run must meet, evaluated when the run starts; null runs every request. A property
    /// trigger whose request it rejects stays pending while its expression stays true and runs as soon as the condition
    /// is true; an event it rejects is gone.
    /// </summary>
    [Configuration]
    public partial string? OnlyIf { get; set; }

    /// <summary>The actions, run in this order.</summary>
    [Configuration]
    public partial AutomationAction[] Actions { get; set; }

    /// <summary>
    /// The mode and whether the rule is armed, holding, triggered or unknown with the missing paths, and why its last
    /// request did not run.
    /// </summary>
    [State(Position = 1)]
    public partial string? Status { get; internal set; }

    /// <summary>When the first action of the last run that was not a dry run started; null before the first run.</summary>
    [State("Last Triggered", Position = 2)]
    public partial DateTimeOffset? LastTriggeredAt { get; internal set; }

    /// <summary>
    /// The number of runs: those that executed their actions and dry runs. A request that did not run, such as one that
    /// <see cref="OnlyIf"/> rejected, is not counted. Null until the automation restored it. After a crash it can restart
    /// below the last value history recorded, since the runs of the last seconds before it may not be saved yet.
    /// </summary>
    [State("Runs", IsCumulative = true, Position = 3)]
    public partial long? RunCount { get; internal set; }

    [State("Last Outcome", IsDiscrete = true, Position = 4)]
    public partial AutomationRunOutcome? LastRunOutcome { get; internal set; }

    /// <summary>A short summary of the last run: the executed actions, the failures, or the actions a dry run would execute.</summary>
    [State("Last Message", Position = 5)]
    public partial string? LastRunMessage { get; internal set; }

    [Derived]
    public string? Title => Name;

    /// <summary>
    /// The keys of the configuration of the rule that name no setting, such as a misspelled <c>onlyIf</c> or a state such
    /// as <c>status</c>; they make the rule invalid and are saved back as they were written.
    /// </summary>
    [JsonExtensionData]
    [JsonInclude]
    internal Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Takes the run properties and the status of the instance that this one replaces.</summary>
    internal void CopyRuntimeStateFrom(AutomationRule other)
    {
        Status = other.Status;
        LastTriggeredAt = other.LastTriggeredAt;
        RunCount = other.RunCount;
        LastRunOutcome = other.LastRunOutcome;
        LastRunMessage = other.LastRunMessage;
    }

    // A configuration file may hold an explicit null, which the runtime never expects.
    partial void OnTriggersChanging(ref AutomationTrigger[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnActionsChanging(ref AutomationAction[] newValue, ref bool cancel) => newValue ??= [];
}
