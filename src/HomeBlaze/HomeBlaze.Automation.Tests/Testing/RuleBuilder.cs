using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>Short forms of rules, triggers and actions for tests.</summary>
internal static class RuleBuilder
{
    public static AutomationRule Create(
        string name,
        AutomationRuleMode mode,
        AutomationTrigger[] triggers,
        AutomationAction[] actions,
        string? onlyIf = null,
        string? inState = null) =>
        new() { Name = name, Mode = mode, Triggers = triggers, Actions = actions, OnlyIf = onlyIf, InState = inState };

    public static AutomationRule On(string name, AutomationTrigger trigger, params AutomationAction[] actions) =>
        Create(name, AutomationRuleMode.On, [trigger], actions);

    public static PropertyTrigger When(
        string expression, TimeSpan? hold = null, string? rearm = null, TriggerActivation activation = TriggerActivation.EvaluateImmediately) =>
        new() { Expression = expression, For = hold, Rearm = rearm, OnActivation = activation };

    public static SubjectEventTrigger OnEvent(string subjectPath, string eventType, string? filter = null) =>
        new() { SubjectPath = subjectPath, EventType = eventType, Filter = filter };

    public static InvokeOperationAction Invoke(string path, params (string Name, string Value)[] arguments) => new()
    {
        Id = path[(path.LastIndexOf('/') + 1)..],
        OperationPath = path,
        Arguments = arguments.ToDictionary(argument => argument.Name, argument => argument.Value)
    };

    public static SetPropertyAction Set(string path, string value) => new() { Id = "set " + path, PropertyPath = path, Value = value };

    public static ChangeStateAction Enter(string state) => new() { Id = "enter " + state, TargetState = state };

    /// <summary>Compiles the rule under <paramref name="key"/> with the states, which must succeed.</summary>
    public static CompiledRule Compile(string key, AutomationRule rule, params string[] states)
    {
        Assert.True(AutomationCompiler.TryCompileRule(key, rule, states, out var compiled, out var error), error);
        return compiled;
    }

    public static Dictionary<string, AutomationRule> Keyed(params (string Key, AutomationRule Rule)[] rules) =>
        rules.ToDictionary(rule => rule.Key, rule => rule.Rule);
}
