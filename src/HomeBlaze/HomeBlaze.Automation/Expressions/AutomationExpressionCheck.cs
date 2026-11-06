using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Parses the expressions and text templates of automations and checks what they use: the variables <c>now</c> and
/// <c>time</c> everywhere, <c>event</c> and <c>subject</c> only where an event is captured, never <c>value</c>; pipes
/// only in templates, <c>display</c> and <c>raw</c>.
/// </summary>
internal static class AutomationExpressionCheck
{
    private const string DisplayPipe = "display";
    private const string RawPipe = "raw";

    /// <param name="text">The expression; null is empty and fails.</param>
    /// <param name="allowsEvent">Whether an event is captured where the expression is evaluated.</param>
    /// <param name="expression">The expression, or null.</param>
    /// <param name="error">The error, or null.</param>
    public static bool TryParseExpression(
        string? text,
        bool allowsEvent,
        [NotNullWhen(true)] out ParsedExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        if (!ParsedExpression.TryParse(text ?? string.Empty, AutomationPaths.Separator, out expression, out error))
        {
            return false;
        }

        error = expression.Pipes.Length > 0 ? "An expression of an automation takes no pipes" : CheckVariables(expression, allowsEvent);
        if (error is null)
        {
            return true;
        }

        expression = null;
        return false;
    }

    /// <param name="text">The text; null is empty.</param>
    /// <param name="allowsEvent">Whether an event is captured where the template is rendered.</param>
    /// <param name="template">The template, or null.</param>
    /// <param name="error">The error, or null.</param>
    public static bool TryParseTemplate(
        string? text,
        bool allowsEvent,
        [NotNullWhen(true)] out TextTemplate? template,
        [NotNullWhen(false)] out string? error)
    {
        if (!TextTemplate.TryParse(text ?? string.Empty, AutomationPaths.Separator, out template, out error))
        {
            return false;
        }

        foreach (var expression in template.Expressions)
        {
            error = CheckPipes(expression) ?? CheckVariables(expression, allowsEvent);
            if (error is not null)
            {
                template = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets whether the expression reads <c>now</c> or <c>time</c>, so that its result changes with the clock.</summary>
    public static bool UsesTime(ParsedExpression expression) =>
        expression.Paths.Any(path =>
            ExpressionVariables.TryGetVariable(path, AutomationPaths.Separator, out var variable) &&
            variable is ExpressionVariables.Now or ExpressionVariables.Time);

    /// <summary>Gets the paths of the expression that are no variable, in order of first appearance.</summary>
    public static IEnumerable<string> GetPropertyPaths(ParsedExpression expression) =>
        expression.Paths.Where(path => !ExpressionVariables.TryGetVariable(path, AutomationPaths.Separator, out _));

    private static string? CheckPipes(ParsedExpression expression)
    {
        foreach (var pipe in expression.Pipes)
        {
            if (!pipe.Name.Equals(DisplayPipe, StringComparison.OrdinalIgnoreCase) &&
                !pipe.Name.Equals(RawPipe, StringComparison.OrdinalIgnoreCase))
            {
                return $"Unknown pipe '{pipe.Name}'";
            }

            if (pipe.Arguments.Length > 0)
            {
                return $"The pipe '{pipe.Name}' takes no arguments";
            }
        }

        return null;
    }

    private static string? CheckVariables(ParsedExpression expression, bool allowsEvent)
    {
        foreach (var path in expression.Paths)
        {
            if (ExpressionVariables.TryGetVariable(path, AutomationPaths.Separator, out var variable, out var memberPath) &&
                GetVariableError(variable, memberPath, allowsEvent) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static string? GetVariableError(string variable, string? memberPath, bool allowsEvent) => variable switch
    {
        ExpressionVariables.Value => "'value' is not available in automations",
        ExpressionVariables.Now or ExpressionVariables.Time when memberPath is not null => $"'{variable}' has no members",
        ExpressionVariables.Event or ExpressionVariables.Subject when !allowsEvent =>
            $"'{variable}' is only available in event filters and in the arguments of a rule with an event trigger",
        _ => null
    };
}
