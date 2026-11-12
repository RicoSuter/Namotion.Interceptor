using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Validates the configuration of an automation and compiles it into a <see cref="CompiledAutomation"/>: state names,
/// the initial state, and per rule its state scope, the syntax and variables of its expressions, its triggers and its
/// actions. A missing device is no error here; the runtime reports it as an unresolved path.
/// </summary>
internal static class AutomationCompiler
{
    public static bool TryCompile(
        IReadOnlyList<string> states,
        string? initialState,
        IReadOnlyDictionary<string, AutomationRule> rules,
        IReadOnlyList<string> ruleOrder,
        [NotNullWhen(true)] out CompiledAutomation? compiled,
        [NotNullWhen(false)] out string? error) =>
        TryCompile(states, initialState, rules, ruleOrder, targets: null, out compiled, out error);

    /// <summary>
    /// Compiles the configuration of the automation with the rules and roles read from it, checking the targets of actions
    /// that resolve against the roles.
    /// </summary>
    public static bool TryCompile(
        Automation automation,
        IReadOnlyList<string> roles,
        IReadOnlyDictionary<string, AutomationRule> rules,
        [NotNullWhen(true)] out CompiledAutomation? compiled,
        [NotNullWhen(false)] out string? error) =>
        TryCompile(
            automation.States, automation.InitialState, rules, automation.RuleOrder, ActionTargetCheck.Create(automation, roles), out compiled, out error);

    /// <summary>Compiles the configuration; <paramref name="targets"/> also checks the targets of actions that resolve.</summary>
    public static bool TryCompile(
        IReadOnlyList<string> states,
        string? initialState,
        IReadOnlyDictionary<string, AutomationRule> rules,
        IReadOnlyList<string> ruleOrder,
        ActionTargetCheck? targets,
        [NotNullWhen(true)] out CompiledAutomation? compiled,
        [NotNullWhen(false)] out string? error)
    {
        var errors = CheckStates(states, initialState);
        var compiledRules = new List<CompiledRule>();
        foreach (var key in OrderKeys(rules.Keys, ruleOrder))
        {
            if (TryCompileRule(key, rules[key], states, targets, out var rule, out var ruleError))
            {
                compiledRules.Add(rule);
            }
            else
            {
                errors.Add(ruleError);
            }
        }

        if (errors.Count > 0)
        {
            compiled = null;
            error = string.Join("; ", errors);
            return false;
        }

        compiled = new CompiledAutomation(compiledRules, [.. states], initialState);
        error = null;
        return true;
    }

    public static bool TryCompileRule(
        string key,
        AutomationRule rule,
        IReadOnlyList<string> states,
        [NotNullWhen(true)] out CompiledRule? compiled,
        [NotNullWhen(false)] out string? error) =>
        TryCompileRule(key, rule, states, targets: null, out compiled, out error);

    public static bool TryCompileRule(
        string key,
        AutomationRule rule,
        IReadOnlyList<string> states,
        ActionTargetCheck? targets,
        [NotNullWhen(true)] out CompiledRule? compiled,
        [NotNullWhen(false)] out string? error)
    {
        var candidate = Compile(key, rule, states, targets, out var errors);
        if (errors.Count > 0)
        {
            compiled = null;
            error = $"Rule '{candidate.Name}': {string.Join("; ", errors)}";
            return false;
        }

        compiled = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// The errors of a rule as <see cref="TryCompileRule(string, AutomationRule, IReadOnlyList{string}, ActionTargetCheck, out CompiledRule, out string)"/>
    /// finds them, without the name of the rule in front.
    /// </summary>
    public static IReadOnlyList<string> GetRuleErrors(string key, AutomationRule rule, IReadOnlyList<string> states, ActionTargetCheck? targets = null)
    {
        Compile(key, rule, states, targets, out var errors);
        return errors;
    }

    private static CompiledRule Compile(string key, AutomationRule rule, IReadOnlyList<string> states, ActionTargetCheck? targets, out List<string> errors)
    {
        errors = [];
        var candidate = new CompiledRule(key, rule, CompileOnlyIf(rule.OnlyIf, errors));
        if (!Enum.IsDefined(rule.Mode))
        {
            errors.Add($"the mode {rule.Mode} is not defined");
        }

        if (rule.InState is { } state && !states.Contains(state, StringComparer.Ordinal))
        {
            errors.Add($"'{state}' is not a state");
        }

        foreach (var setting in rule.UnknownSettings?.Keys ?? Enumerable.Empty<string>())
        {
            if (setting != ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName)
            {
                errors.Add($"'{setting}' is no setting of a rule");
            }
        }

        CompileTriggers(candidate, rule.Triggers, errors);
        CheckActions(key, rule.Actions, candidate.AllowsEvent, states, targets, errors);
        return candidate;
    }

    /// <summary>The keys in the configured order first, then the remaining keys in ordinal order.</summary>
    public static IReadOnlyList<string> OrderKeys(IEnumerable<string> keys, IReadOnlyList<string> ruleOrder)
    {
        var remaining = keys.ToHashSet(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var key in ruleOrder)
        {
            if (remaining.Remove(key))
            {
                ordered.Add(key);
            }
        }

        ordered.AddRange(remaining.Order(StringComparer.Ordinal));
        return ordered;
    }

    /// <summary>Names an action in messages by its identifier, or by its position when it has none.</summary>
    public static string DescribeAction(AutomationAction? action, int index) =>
        action is { Id.Length: > 0 } ? $"action '{action.Id}'" : $"action {index + 1}";

    /// <summary>The errors of the state names and the initial state.</summary>
    public static List<string> CheckStates(IReadOnlyList<string> states, string? initialState)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                errors.Add("A state needs a name");
            }
            else if (!names.Add(state))
            {
                errors.Add($"The state '{state}' is defined twice");
            }
        }

        if (GetInitialStateError(names, states.Count, initialState) is { } initialError)
        {
            errors.Add(initialError);
        }

        return errors;
    }

    private static string? GetInitialStateError(HashSet<string> names, int stateCount, string? initialState)
    {
        if (stateCount == 0)
        {
            return initialState is null ? null : "An initial state needs states";
        }

        if (initialState is null)
        {
            return "Choose the initial state";
        }

        return names.Contains(initialState) ? null : $"The initial state '{initialState}' is not a state";
    }

    private static ParsedExpression? CompileOnlyIf(string? onlyIf, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(onlyIf))
        {
            return null;
        }

        if (AutomationExpressionCheck.TryParseExpression(onlyIf, allowsEvent: false, out var expression, out var error))
        {
            return expression;
        }

        errors.Add($"Only if: {error}");
        return null;
    }

    private static void CompileTriggers(CompiledRule rule, AutomationTrigger[] triggers, List<string> errors)
    {
        if (triggers.Length == 0)
        {
            errors.Add("it has no trigger");
            return;
        }

        for (var index = 0; index < triggers.Length; index++)
        {
            var error = triggers[index] switch
            {
                PropertyTrigger property => CompilePropertyTrigger(rule, index, property),
                SubjectEventTrigger subjectEvent => CompileEventTrigger(rule, index, subjectEvent),
                UnreadableTrigger unreadable => unreadable.Error,
                _ => "the type is unknown"
            };

            if (error is not null)
            {
                errors.Add($"trigger {index + 1}: {error}");
            }
        }
    }

    private static string? CompilePropertyTrigger(CompiledRule rule, int index, PropertyTrigger trigger)
    {
        if (!AutomationExpressionCheck.TryParseExpression(trigger.Expression, allowsEvent: false, out var expression, out var error))
        {
            return error;
        }

        if (trigger.For < TimeSpan.Zero)
        {
            return "the hold cannot be negative";
        }

        if (trigger.For > AutomationDuration.Maximum)
        {
            return $"the hold cannot be longer than {AutomationDuration.Format(AutomationDuration.Maximum)}";
        }

        if (!Enum.IsDefined(trigger.OnActivation))
        {
            return $"the activation policy {trigger.OnActivation} is not defined";
        }

        ParsedExpression? rearm = null;
        if (!string.IsNullOrWhiteSpace(trigger.Rearm) &&
            !AutomationExpressionCheck.TryParseExpression(trigger.Rearm, allowsEvent: false, out rearm, out error))
        {
            return $"rearm: {error}";
        }

        rule.PropertyTriggers.Add(new CompiledPropertyTrigger(rule, index, trigger, expression, rearm));
        return null;
    }

    private static string? CompileEventTrigger(CompiledRule rule, int index, SubjectEventTrigger trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger.SubjectPath))
        {
            return "the subject path is missing";
        }

        if (string.IsNullOrWhiteSpace(trigger.EventType))
        {
            return "the event type is missing";
        }

        ParsedExpression? filter = null;
        if (!string.IsNullOrWhiteSpace(trigger.Filter) &&
            !AutomationExpressionCheck.TryParseExpression(trigger.Filter, allowsEvent: true, out filter, out var error))
        {
            return $"filter: {error}";
        }

        rule.EventTriggers.Add(new CompiledEventTrigger(rule, index, trigger, filter));
        return null;
    }

    private static void CheckActions(
        string key, AutomationAction[] actions, bool allowsEvent, IReadOnlyList<string> states, ActionTargetCheck? targets, List<string> errors)
    {
        if (actions.Length == 0)
        {
            errors.Add("it has no action");
            return;
        }

        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            if ((CheckAction(action, allowsEvent, states) ?? targets?.Check(key, action)) is { } error)
            {
                errors.Add($"{DescribeAction(action, index)}: {error}");
            }
        }
    }

    private static string? CheckAction(AutomationAction? action, bool allowsEvent, IReadOnlyList<string> states) =>
        action is not null && !Enum.IsDefined(action.OnError) ? $"the error policy {action.OnError} is not defined" : CheckActionType(action, allowsEvent, states);

    private static string? CheckActionType(AutomationAction? action, bool allowsEvent, IReadOnlyList<string> states) => action switch
    {
        InvokeOperationAction invoke => CheckInvoke(invoke, allowsEvent),
        SetPropertyAction set => CheckSet(set, allowsEvent),
        ChangeStateAction change => states.Contains(change.TargetState, StringComparer.Ordinal) ? null : $"'{change.TargetState}' is not a state",
        UnreadableAction unreadable => unreadable.Error,
        _ => "the type is unknown"
    };

    private static string? CheckInvoke(InvokeOperationAction action, bool allowsEvent)
    {
        if (string.IsNullOrWhiteSpace(action.OperationPath))
        {
            return "the operation path is missing";
        }

        if (action.Arguments is null)
        {
            return "the arguments are missing";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in action.Arguments)
        {
            if (!names.Add(name))
            {
                return $"argument '{name}' is given twice";
            }

            if (CheckValue(value, allowsEvent) is { } error)
            {
                return $"argument '{name}': {error}";
            }
        }

        return null;
    }

    private static string? CheckSet(SetPropertyAction action, bool allowsEvent)
    {
        if (string.IsNullOrWhiteSpace(action.PropertyPath))
        {
            return "the property path is missing";
        }

        if (action.Value is null)
        {
            return "the value is missing";
        }

        return CheckValue(action.Value, allowsEvent) is { } error ? $"value: {error}" : null;
    }

    private static string? CheckValue(string? value, bool allowsEvent)
    {
        if (value is null)
        {
            return "the value is missing";
        }

        return AutomationExpressionCheck.TryParseTemplate(value, allowsEvent, out _, out var error) ? null : error;
    }
}
