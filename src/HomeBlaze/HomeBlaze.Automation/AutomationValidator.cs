using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation;

/// <summary>
/// Checks the configuration of an automation without applying or running it: everything an automation checks before it
/// applies a configuration, and the checks of the editor on top of that, which are unique rule names, unique action
/// identifiers within a rule and state names without white space at their ends. An automation still applies a
/// configuration that fails only the checks of the editor.
/// </summary>
public static class AutomationValidator
{
    /// <summary>Gets the errors of the configuration, those of the automation first and then those of each rule in run order.</summary>
    public static IReadOnlyList<AutomationValidationError> Validate(
        IReadOnlyList<string> states,
        string? initialState,
        IReadOnlyDictionary<string, AutomationRule> rules,
        IReadOnlyList<string> ruleOrder)
    {
        states ??= [];
        ruleOrder ??= [];
        var errors = AutomationCompiler.CheckStates(states, initialState)
            .Select(message => new AutomationValidationError(null, message))
            .Concat(CheckStateNames(states))
            .ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AutomationCompiler.OrderKeys(rules.Keys, ruleOrder))
        {
            var rule = rules[key] ?? new AutomationRule();
            errors.AddRange(AutomationCompiler.GetRuleErrors(key, rule, states)
                .Concat(CheckActionIds(rule.Actions))
                .Select(message => new AutomationValidationError(key, message)));
            if (!string.IsNullOrWhiteSpace(rule.Name) && !names.Add(rule.Name.Trim()))
            {
                errors.Add(new AutomationValidationError(key, $"Another rule is named '{rule.Name.Trim()}'"));
            }
        }

        return errors;
    }

    /// <summary>Gets the error of an expression of a trigger, a rearm, a guard or an event filter, or null when it is valid.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="allowsEvent">Whether an event is captured where it is evaluated: in event filters and in rules with an event trigger.</param>
    public static string? ValidateExpression(string? text, bool allowsEvent) =>
        AutomationExpressionCheck.TryParseExpression(text, allowsEvent, out _, out var error) ? null : error;

    /// <summary>Gets the error of the text template of an argument, or null when it is valid.</summary>
    /// <param name="text">The template.</param>
    /// <param name="allowsEvent">Whether the rule has an event trigger.</param>
    public static string? ValidateTemplate(string? text, bool allowsEvent) =>
        AutomationExpressionCheck.TryParseTemplate(text, allowsEvent, out _, out var error) ? null : error;

    private static IEnumerable<AutomationValidationError> CheckStateNames(IReadOnlyList<string> states) =>
        states
            .Where(state => !string.IsNullOrWhiteSpace(state) && state.Trim().Length != state.Length)
            .Select(state => new AutomationValidationError(null, $"The state '{state}' starts or ends with white space"));

    private static IEnumerable<string> CheckActionIds(IEnumerable<AutomationAction?> actions) =>
        actions
            .OfType<AutomationAction>()
            .Where(action => action.Id.Length > 0)
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"The action id '{group.Key}' is used {group.Count()} times");
}
