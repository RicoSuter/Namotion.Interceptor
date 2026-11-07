using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// An expression parsed once: paths and literals, computed with <c>+ - * /</c>, compared with
/// <c>== != &lt; &lt;= &gt; &gt;=</c> and combined with <c>and</c> (<c>&amp;&amp;</c>), <c>or</c> (<c>||</c>),
/// <c>not</c> (<c>!</c>) and parentheses, followed by pipes such as <c>| raw</c>. A bare word after a comparison operator
/// is text, such as the name of an enum value, unless it holds the path separator or an index, is a variable of
/// <see cref="Variables"/>, or is an operand of arithmetic.
/// </summary>
public sealed class ParsedExpression
{
    internal ParsedExpression(
        string text,
        char pathSeparator,
        ImmutableArray<string> variables,
        ExpressionNode body,
        ImmutableArray<ExpressionPipe> pipes,
        ImmutableArray<string> paths)
    {
        Text = text;
        PathSeparator = pathSeparator;
        Variables = variables;
        Body = body;
        Pipes = pipes;
        Paths = paths;
    }

    /// <summary>The text the expression was parsed from.</summary>
    public string Text { get; }

    /// <summary>The character between the segments of the paths of the expression.</summary>
    public char PathSeparator { get; }

    /// <summary>
    /// The names that are variables of the expression: those the host declared when it parsed the expression, or
    /// <see cref="ExpressionVariables.Value"/> and <see cref="ExpressionVariables.Subject"/>.
    /// </summary>
    public ImmutableArray<string> Variables { get; }

    /// <summary>The distinct paths the expression reads, in order of first appearance.</summary>
    public ImmutableArray<string> Paths { get; }

    /// <summary>The pipes after the expression, in the order they apply.</summary>
    public ImmutableArray<ExpressionPipe> Pipes { get; }

    /// <summary>The path when the expression is one path with optional pipes, otherwise null.</summary>
    public string? SinglePath => Body is PathNode path ? path.Path : null;

    internal ExpressionNode Body { get; }

    /// <summary>
    /// Parses the text with paths whose segments the separator divides, such as <c>.</c>. A word after an operator is text,
    /// such as the name of an enum value, unless it holds the separator or an index or is the variable <c>value</c> or
    /// <c>subject</c>, which are paths. On failure the error reads
    /// <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The separator is white space, a letter, a digit, a quote, a bracket, or one of <c>| &amp; = ! &lt; &gt; + - *</c>.</exception>
    public static bool TryParse(
        string text,
        char pathSeparator,
        [NotNullWhen(true)] out ParsedExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        ValidatePathSeparator(pathSeparator);
        return TryParseValidated(text, pathSeparator, ExpressionVariables.Default, out expression, out error);
    }

    /// <summary>
    /// Parses the text with paths whose segments the separator divides and with the variables the host declares, which
    /// replace <see cref="ExpressionVariables.Value"/> and <see cref="ExpressionVariables.Subject"/>. A path whose first
    /// segment is one of these names starts with the variable, and a word after an operator is text unless it holds the
    /// separator or an index or is one of these names. Every other word is a path or a text, as for a host that does not
    /// declare it. On failure the error reads <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="pathSeparator">The character between the segments of the paths.</param>
    /// <param name="variables">The names that are variables, each a word of letters, digits and underscores that starts with a
    /// letter and is none of the keywords <c>and</c>, <c>or</c>, <c>not</c>, <c>true</c>, <c>false</c> and <c>null</c>.</param>
    /// <param name="expression">The expression, or null.</param>
    /// <param name="error">The error, or null.</param>
    /// <exception cref="ArgumentException">The separator is white space, a letter, a digit, a quote, a bracket, or one of
    /// <c>| &amp; = ! &lt; &gt; + - *</c>, or a variable is no such word.</exception>
    public static bool TryParse(
        string text,
        char pathSeparator,
        ImmutableArray<string> variables,
        [NotNullWhen(true)] out ParsedExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        ValidatePathSeparator(pathSeparator);
        ExpressionVariables.Validate(variables);
        return TryParseValidated(text, pathSeparator, variables, out expression, out error);
    }

    /// <summary>
    /// Gets whether the first segment of a path of the expression is one of its <see cref="Variables"/>, and which one, with
    /// the rest of the path.
    /// </summary>
    /// <param name="path">The path, such as one of <see cref="Paths"/>.</param>
    /// <param name="variable">The variable, or null.</param>
    /// <param name="memberPath">The path after the variable and its separator, or null for the variable alone or a path without one.</param>
    public bool TryGetVariable(string path, [NotNullWhen(true)] out string? variable, out string? memberPath) =>
        ExpressionVariables.TryGetVariable(path, PathSeparator, Variables, out variable, out memberPath);

    internal static bool TryParseValidated(
        string text,
        char pathSeparator,
        ImmutableArray<string> variables,
        [NotNullWhen(true)] out ParsedExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            expression = new ExpressionParser(text, pathSeparator, variables).Parse();
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            expression = null;
            error = exception.Message;
            return false;
        }
    }

    internal static void ValidatePathSeparator(char pathSeparator)
    {
        if (char.IsLetterOrDigit(pathSeparator) || char.IsWhiteSpace(pathSeparator) || "'\"[](){}|&=!<>+-*".Contains(pathSeparator))
        {
            throw new ArgumentException($"The path separator '{pathSeparator}' cannot be used in an expression.", nameof(pathSeparator));
        }
    }

    /// <summary>Evaluates the expression and applies its pipes.</summary>
    public ExpressionValue Evaluate<TContext>(ref TContext context)
        where TContext : struct, IExpressionContext
    {
        var value = ExpressionEvaluator.Evaluate(Body, ref context);
        foreach (var pipe in Pipes)
        {
            value = context.ApplyPipe(pipe.Name, pipe.Arguments, value);
        }

        return value;
    }

    /// <summary>Evaluates the expression without applying its pipes.</summary>
    public ExpressionValue EvaluateWithoutPipes<TContext>(ref TContext context)
        where TContext : struct, IExpressionContext =>
        ExpressionEvaluator.Evaluate(Body, ref context);

    /// <summary>
    /// Evaluates the expression as a condition: true, false, or null for unknown, when a value it needs is missing or a
    /// comparison has types that do not fit. A bare operand is true only when it is the boolean true. Pipes are not applied.
    /// </summary>
    public bool? EvaluateCondition<TContext>(ref TContext context)
        where TContext : struct, IExpressionContext =>
        ExpressionEvaluator.EvaluateTruth(Body, ref context);
}
