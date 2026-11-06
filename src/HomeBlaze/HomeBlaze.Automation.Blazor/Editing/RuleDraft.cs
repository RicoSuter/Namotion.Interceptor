using HomeBlaze.Abstractions.Metadata;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// A rule while it is edited. Its key never changes, so that the history and the checkpoint of a saved rule stay its own.
/// Triggers and actions are immutable values in entries that keep their identity.
/// </summary>
public sealed class RuleDraft
{
    private const string CallPrefix = "call";
    private const string SetPrefix = "set";
    private const string StatePrefix = "state";

    private RuleDraft(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public string? Name { get; set; }

    public AutomationRuleMode Mode { get; set; }

    public string? InState { get; set; }

    public string? Guard { get; set; }

    public List<DraftItem<AutomationTrigger>> Triggers { get; } = [];

    public List<DraftItem<AutomationAction>> Actions { get; } = [];

    /// <summary>The name, or the key of a rule without a name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;

    /// <summary>Whether the rule has an event trigger, so that its arguments may read <c>event</c> and <c>subject</c>.</summary>
    public bool AllowsEvent => Triggers.Exists(trigger => trigger.Value is SubjectEventTrigger);

    /// <summary>The states the actions of the rule change to.</summary>
    public IEnumerable<string> TargetStates =>
        Actions.Select(action => action.Value).OfType<ChangeStateAction>().Select(action => action.TargetState);

    public static RuleDraft From(string key, AutomationRule rule)
    {
        var draft = new RuleDraft(key) { Name = rule.Name, Mode = rule.Mode, InState = rule.InState, Guard = rule.Guard };
        draft.Triggers.AddRange(rule.Triggers.OfType<AutomationTrigger>().Select(trigger => new DraftItem<AutomationTrigger>(trigger)));
        draft.Actions.AddRange(rule.Actions.OfType<AutomationAction>().Select(action => new DraftItem<AutomationAction>(CopyArguments(action))));
        return draft;
    }

    /// <summary>A new rule in dry run with one empty property condition, so that it acts on nothing before it is switched on.</summary>
    public static RuleDraft Create(string key, string name)
    {
        var draft = new RuleDraft(key) { Name = name, Mode = AutomationRuleMode.DryRun };
        draft.AddTrigger(new PropertyTrigger());
        return draft;
    }

    public void AddTrigger(AutomationTrigger trigger) => Triggers.Add(new DraftItem<AutomationTrigger>(trigger));

    public void AddCallOperation() => AddAction(new InvokeOperationAction(), CallPrefix);

    public void AddSetProperty() => AddAction(new SetPropertyAction(), SetPrefix);

    public void AddChangeState(string targetState) => AddAction(new ChangeStateAction { TargetState = targetState }, StatePrefix);

    public void MoveAction(DraftItem<AutomationAction> action, int offset) => DraftLists.Move(Actions, action, offset);

    /// <summary>
    /// The rule as it is saved: a blank name, guard, rearm or filter is none. With the lookup of an operation by its path, an
    /// operation action whose operation was changed keeps only the arguments that its operation takes, which the binder
    /// requires, and all of them when the path leads to no operation. Any other action is saved as it was loaded.
    /// </summary>
    public AutomationRule ToRule(Func<string, MethodMetadata?>? getMethod = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Mode = Mode,
        InState = InState,
        Guard = string.IsNullOrWhiteSpace(Guard) ? null : Guard,
        Triggers = [.. Triggers.Select(trigger => Normalize(trigger.Value))],
        Actions = [.. Actions.Select(action => action.IsOperationChanged ? Prune(action.Value, getMethod) : action.Value)]
    };

    // The deserialized arguments are a mutable dictionary that the live action shares, so the draft holds its own copy.
    private static AutomationAction CopyArguments(AutomationAction action) => action is InvokeOperationAction invoke
        ? invoke with { Arguments = new Dictionary<string, string>(invoke.Arguments) }
        : action;

    private static AutomationAction Prune(AutomationAction action, Func<string, MethodMetadata?>? getMethod) =>
        action is InvokeOperationAction invoke && getMethod is not null
            ? invoke with { Arguments = ArgumentFields.KeepKnown(getMethod(invoke.OperationPath), invoke.Arguments) }
            : action;

    private void AddAction(AutomationAction action, string prefix) =>
        Actions.Add(new DraftItem<AutomationAction>(action with { Id = DraftKeys.NewActionId(prefix, Actions.Select(existing => existing.Value.Id)) }));

    private static AutomationTrigger Normalize(AutomationTrigger trigger) => trigger switch
    {
        PropertyTrigger { Rearm: { } rearm } property when string.IsNullOrWhiteSpace(rearm) => property with { Rearm = null },
        SubjectEventTrigger { Filter: { } filter } subjectEvent when string.IsNullOrWhiteSpace(filter) => subjectEvent with { Filter = null },
        _ => trigger
    };
}
