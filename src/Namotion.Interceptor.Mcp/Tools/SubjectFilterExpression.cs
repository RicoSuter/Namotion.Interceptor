using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// A value filter over the properties of a subject, written in the expression language of
/// <c>Namotion.Interceptor.Expressions</c>: comparisons, <c>and</c>, <c>or</c>, <c>not</c>, parentheses and bare booleans
/// over paths that resolve from the subject through the path provider. <c>subject</c> is the subject itself. A filter
/// matches only when it is true: a missing value makes a comparison unknown, and <c>== null</c> is true for it.
/// </summary>
internal sealed class SubjectFilterExpression
{
    private const string ErrorPrefix = "where: ";

    private readonly ParsedExpression _expression;

    private SubjectFilterExpression(ParsedExpression expression, IReadOnlyCollection<string> paths)
    {
        _expression = expression;
        Paths = paths;
    }

    /// <summary>
    /// The distinct paths the filter reads from a subject, in order of first appearance, with <c>subject</c> and its
    /// separator removed.
    /// </summary>
    public IReadOnlyCollection<string> Paths { get; }

    /// <summary>
    /// Parses a filter whose paths use the separator of the path provider. On failure the error starts with
    /// <c>where: </c>, and a syntax error ends with <c>at position &lt;zero-based index&gt;</c>.
    /// </summary>
    public static bool TryParse(
        string text,
        char pathSeparator,
        [NotNullWhen(true)] out SubjectFilterExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        expression = null;
        if (!ParsedExpression.TryParse(text, pathSeparator, out var parsed, out var parseError))
        {
            error = ErrorPrefix + parseError;
            return false;
        }

        if (parsed.Pipes.Length > 0)
        {
            error = ErrorPrefix + "a condition takes no pipes";
            return false;
        }

        if (!TryCollectPaths(parsed, pathSeparator, out var paths, out error))
        {
            return false;
        }

        expression = new SubjectFilterExpression(parsed, paths);
        return true;
    }

    /// <summary>Evaluates the filter against the current property values of <paramref name="subject"/>.</summary>
    public bool Matches(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        var context = new FilterContext(subject, pathProvider);
        return _expression.EvaluateCondition(ref context) == true;
    }

    private static bool TryCollectPaths(
        ParsedExpression parsed,
        char pathSeparator,
        out List<string> paths,
        [NotNullWhen(false)] out string? error)
    {
        paths = [];
        error = null;
        foreach (var path in parsed.Paths)
        {
            var memberPath = path;
            if (ExpressionVariables.TryGetVariable(path, pathSeparator, out var variable, out var variableMemberPath))
            {
                if (variable == ExpressionVariables.Value)
                {
                    error = ErrorPrefix + $"'{ExpressionVariables.Value}' is not available in where";
                    return false;
                }

                if (variableMemberPath is null)
                {
                    continue;
                }

                memberPath = variableMemberPath;
            }

            if (!paths.Contains(memberPath))
            {
                paths.Add(memberPath);
            }
        }

        return true;
    }

    private readonly struct FilterContext(RegisteredSubject subject, PathProviderBase pathProvider) : IExpressionContext
    {
        public ExpressionValue ResolvePath(string path) =>
            McpToolHelper.TryGetFilterPropertyFromPath(subject, pathProvider, path) is { } property
                ? ExpressionValue.FromProperty(property, property.GetValue())
                : ExpressionValue.Missing;

        // TryParse refuses 'value', so only 'subject' arrives here.
        public ExpressionValue ResolveVariable(string variable, string? memberPath) =>
            memberPath is null ? ExpressionValue.FromValue(subject.Subject) : ResolvePath(memberPath);

        // TryParse refuses pipes.
        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
