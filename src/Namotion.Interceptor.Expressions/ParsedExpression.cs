using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// An expression parsed once: paths and literals, compared with <c>== != &lt; &lt;= &gt; &gt;=</c> and combined with
/// <c>and</c> (<c>&amp;&amp;</c>), <c>or</c> (<c>||</c>), <c>not</c> (<c>!</c>) and parentheses, followed by pipes such as
/// <c>| raw</c>. A bare word after an operator is text, such as the name of an enum value, unless it holds the path separator or an index
/// or is the variable <c>value</c> or <c>subject</c>. Arithmetic is rejected.
/// </summary>
public sealed class ParsedExpression
{
    internal ParsedExpression(string text, char pathSeparator, ExpressionNode body, ImmutableArray<ExpressionPipe> pipes, ImmutableArray<string> paths)
    {
        Text = text;
        PathSeparator = pathSeparator;
        Body = body;
        Pipes = pipes;
        Paths = paths;
    }

    /// <summary>The text the expression was parsed from.</summary>
    public string Text { get; }

    /// <summary>The character between the segments of the paths of the expression.</summary>
    public char PathSeparator { get; }

    /// <summary>The distinct paths the expression reads, in order of first appearance.</summary>
    public ImmutableArray<string> Paths { get; }

    /// <summary>The pipes after the expression, in the order they apply.</summary>
    public ImmutableArray<ExpressionPipe> Pipes { get; }

    /// <summary>The path when the expression is one path with optional pipes, otherwise null.</summary>
    public string? SinglePath => Body is PathNode path ? path.Path : null;

    internal ExpressionNode Body { get; }

    /// <summary>
    /// Parses the text with paths written with <c>/</c>. On failure the error reads
    /// <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
    /// </summary>
    public static bool TryParse(
        string text,
        [NotNullWhen(true)] out ParsedExpression? expression,
        [NotNullWhen(false)] out string? error) =>
        TryParse(text, '/', out expression, out error);

    /// <summary>
    /// Parses the text with paths whose segments the separator divides, such as <c>.</c>. A word after an operator is text,
    /// such as the name of an enum value, unless it holds the separator or an index or is the variable <c>value</c> or
    /// <c>subject</c>, which are paths. On failure the error reads
    /// <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
    /// </summary>
    public static bool TryParse(
        string text,
        char pathSeparator,
        [NotNullWhen(true)] out ParsedExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            expression = new ExpressionParser(text, pathSeparator).Parse();
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
