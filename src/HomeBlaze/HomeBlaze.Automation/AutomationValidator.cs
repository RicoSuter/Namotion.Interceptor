using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using Namotion.Interceptor.Expressions;

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
    public static ImmutableArray<AutomationValidationError> Validate(
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
        var errors = ImmutableArray.CreateBuilder<AutomationValidationError>();
        errors.AddRange(AutomationCompiler.CheckStates(states, initialState)
            .Select(message => new AutomationValidationError(null, message))
            .Concat(CheckStateNames(states)));
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

        return errors.DrainToImmutable();
    }

    /// <summary>
    /// Parses an expression of a trigger, a rearm, Only if, an event filter or an argument as the automation does. The
    /// error says why the expression is invalid.
    /// </summary>
    /// <param name="text">The expression.</param>
    /// <param name="allowsEvent">Whether an event is captured where it is evaluated: in event filters and in the arguments of a rule with an event trigger.</param>
    /// <param name="parsed">The parsed expression, for <see cref="AutomationPreview.Evaluate"/>.</param>
    /// <param name="error">Why the expression is invalid.</param>
    public static bool TryParseExpression(
        string? text, bool allowsEvent, [NotNullWhen(true)] out ParsedExpression? parsed, [NotNullWhen(false)] out string? error) =>
        AutomationExpressionCheck.TryParseExpression(text, allowsEvent, out parsed, out error);

    /// <summary>Parses the text template of an argument as the automation does. The error says why the template is invalid.</summary>
    /// <param name="text">The template.</param>
    /// <param name="allowsEvent">Whether the rule has an event trigger.</param>
    /// <param name="parsed">The parsed template, for <see cref="AutomationPreview.Render"/>.</param>
    /// <param name="error">Why the template is invalid.</param>
    public static bool TryParseTemplate(
        string? text, bool allowsEvent, [NotNullWhen(true)] out TextTemplate? parsed, [NotNullWhen(false)] out string? error) =>
        AutomationExpressionCheck.TryParseTemplate(text, allowsEvent, out parsed, out error);

    /// <summary>
    /// Gets the hints for a valid condition of a trigger, a rearm or Only if: one when it compares <c>now</c> or
    /// <c>time</c> with <c>==</c> or <c>!=</c>, and one when its use of the clock is checked every second. Empty for an
    /// invalid condition, whose error says more.
    /// </summary>
    public static ImmutableArray<string> GetConditionHints(string? text) => ConditionHints.Get(text);

    /// <summary>
    /// Whether the automation binds an argument with the key to the operation: a key that names a parameter that requires
    /// input, or <c>parameter.Member</c> for a member of a record parameter, without regard to case.
    /// </summary>
    public static bool IsArgumentKey(MethodMetadata operation, string key) =>
        ArgumentBinder.MatchKey(operation.Parameters, key, out _) is ArgumentKeyMatch.Parameter or ArgumentKeyMatch.Member;

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
