using System.Collections.Immutable;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation;

/// <summary>
/// Checks the configuration of an automation without applying or running it: everything an automation checks before it
/// applies a configuration, the targets of actions against the roles included, and the checks of the editor on top of
/// that, which are unique rule names, unique action identifiers within a rule, state names without white space at their
/// ends, and roles that can be used while a rule calls an operation or writes a property. An automation still applies a
/// configuration that fails only the checks of the editor.
/// </summary>
public static class AutomationValidator
{
    /// <summary>The variables that the expressions and templates of automations declare: <c>now</c>, <c>time</c>, <c>event</c>, <c>subject</c> and <c>value</c>.</summary>
    public static ImmutableArray<string> Variables => AutomationVariables.All;

    /// <summary>
    /// Gets the errors of a configuration of the automation, those of the automation first and then those of each rule in
    /// run order. Relative paths start at the saved rules of the automation, so the relative targets of a rule that is not
    /// saved yet are checked when the saved configuration is applied and when the action runs.
    /// </summary>
    /// <param name="automation">The automation whose path resolver and authorizer check the targets.</param>
    /// <param name="roles">The roles of the configuration, which may differ from the saved <see cref="Automation.Roles"/>.</param>
    /// <param name="states">The states of the configuration.</param>
    /// <param name="initialState">The initial state of the configuration.</param>
    /// <param name="rules">The rules of the configuration by key.</param>
    /// <param name="ruleOrder">The keys of the rules in run order.</param>
    public static IReadOnlyList<AutomationValidationError> Validate(
        Automation automation,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> states,
        string? initialState,
        IReadOnlyDictionary<string, AutomationRule> rules,
        IReadOnlyList<string> ruleOrder)
    {
        ArgumentNullException.ThrowIfNull(automation);
        roles ??= [];
        states ??= [];
        ruleOrder ??= [];
        var permissions = AutomationTargets.GetPermissions(automation, roles);
        var targets = new ActionTargetCheck(automation.PathResolver, permissions, key => AutomationPreview.GetAnchor(automation, key));
        var errors = AutomationCompiler.CheckStates(states, initialState)
            .Select(message => new AutomationValidationError(null, message))
            .Concat(CheckStateNames(states))
            .ToList();
        if (AutomationTargets.GetRolesError(permissions, rules.Values) is { } rolesError)
        {
            errors.Add(new AutomationValidationError(null, rolesError));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AutomationCompiler.OrderKeys(rules.Keys, ruleOrder))
        {
            var rule = rules[key] ?? new AutomationRule();
            errors.AddRange(AutomationCompiler.GetRuleErrors(key, rule, states, targets)
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
            .Where(action => !string.IsNullOrEmpty(action.Id))
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"The action id '{group.Key}' is used {group.Count()} times");
}
