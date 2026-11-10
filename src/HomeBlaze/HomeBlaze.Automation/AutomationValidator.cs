using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation;

/// <summary>
/// Checks the configuration of an automation without applying or running it: everything an automation checks before it
/// applies a configuration, and the checks of the editor on top of that, which are unique rule names, unique action
/// identifiers within a rule and state names without white space at their ends. An automation still applies a
/// configuration that fails only the checks of the editor.
/// </summary>
public static class AutomationValidator
{
    /// <summary>The variables that the expressions and templates of automations declare: <c>now</c>, <c>time</c>, <c>event</c>, <c>subject</c> and <c>value</c>.</summary>
    public static ImmutableArray<string> Variables => AutomationVariables.All;

    /// <summary>Gets the errors of the configuration, those of the automation first and then those of each rule in run order.</summary>
    public static ImmutableArray<AutomationValidationError> Validate(
        IReadOnlyList<string> states,
        string? initialState,
        IReadOnlyDictionary<string, AutomationRule> rules,
        IReadOnlyList<string> ruleOrder)
    {
        states ??= [];
        ruleOrder ??= [];
        var errors = ImmutableArray.CreateBuilder<AutomationValidationError>();
        errors.AddRange(AutomationCompiler.CheckStates(states, initialState)
            .Select(message => new AutomationValidationError(null, message))
            .Concat(CheckStateNames(states)));
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
