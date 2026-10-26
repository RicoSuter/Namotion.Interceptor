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
        ParsedExpression? parsed;
        string? parseError;
        try
        {
            if (!ParsedExpression.TryParse(text, pathSeparator, out parsed, out parseError))
            {
                error = ErrorPrefix + parseError;
                return false;
            }
        }
        catch (ArgumentException exception) when (exception.ParamName == nameof(pathSeparator))
        {
            error = ErrorPrefix + $"the path separator '{pathSeparator}' is not supported";
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

    /// <summary>
    /// Evaluates the filter against the current property values of <paramref name="subject"/>. A path reads as missing
    /// when its property is on a subject that is not shown, and a reference to a subject that is not shown reads as null.
    /// </summary>
    public bool Matches(RegisteredSubject subject, PathProviderBase pathProvider, McpVisibility visibility)
    {
        var context = new FilterContext(subject, pathProvider, visibility);
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

                if (variableMemberPath.Length == 0)
                {
                    error = ErrorPrefix + $"expected a name after '{variable}{pathSeparator}'";
                    return false;
                }

                memberPath = variableMemberPath;
            }

            error = GetMemberPathError(path, memberPath, pathSeparator);
            if (error is not null)
            {
                return false;
            }

            if (!paths.Contains(memberPath))
            {
                paths.Add(memberPath);
            }
        }

        return true;
    }

    // A path starts at the subject and ends at a property, so none starts with the separator, goes up with '..' or ends
    // at an entry such as 'Devices[a]', which no filter can compare with anything but null.
    private static string? GetMemberPathError(string path, string memberPath, char pathSeparator)
    {
        if (memberPath[0] == pathSeparator)
        {
            return ErrorPrefix + $"a path starts at the subject and cannot start with '{pathSeparator}'";
        }

        if (HasParentSegment(memberPath, pathSeparator))
        {
            return ErrorPrefix + "a path starts at the subject and cannot go up with '..'";
        }

        return memberPath[^1] == ']'
            ? ErrorPrefix + $"a path ends at a property, not at an entry such as '{path}'"
            : null;
    }

    private static bool HasParentSegment(string memberPath, char pathSeparator)
    {
        var segmentStart = 0;
        var depth = 0;
        for (var index = 0; index <= memberPath.Length; index++)
        {
            if (index < memberPath.Length)
            {
                var character = memberPath[index];
                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']' && depth > 0)
                {
                    depth--;
                }

                if (character != pathSeparator || depth > 0)
                {
                    continue;
                }
            }

            if (memberPath.AsSpan(segmentStart, index - segmentStart) is "..")
            {
                return true;
            }

            segmentStart = index + 1;
        }

        return false;
    }

    private readonly struct FilterContext(RegisteredSubject subject, PathProviderBase pathProvider, McpVisibility visibility)
        : IExpressionContext
    {
        public ExpressionValue ResolvePath(string path)
        {
            if (McpToolHelper.TryGetFilterPropertyFromPath(subject, pathProvider, path, visibility) is not { } property)
            {
                return ExpressionValue.Missing;
            }

            // Browse leaves out a referenced subject that is not shown, so the filter reads the reference as unset.
            var value = property.GetValue();
            return property.IsSubjectReference && value is IInterceptorSubject referenced && !visibility.IsShown(referenced)
                ? ExpressionValue.FromProperty(property, null)
                : ExpressionValue.FromProperty(property, value);
        }

        // TryParse refuses 'value', so only 'subject' arrives here.
        public ExpressionValue ResolveVariable(string variable, string? memberPath) =>
            memberPath is null ? ExpressionValue.FromValue(subject.Subject) : ResolvePath(memberPath);

        // TryParse refuses pipes.
        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
