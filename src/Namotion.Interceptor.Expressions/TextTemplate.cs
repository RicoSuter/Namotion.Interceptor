using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// A text with expressions between double braces, such as <c>Charging {{ subject/ChargingPower }}</c>, parsed once. A
/// text without <c>{{</c> is plain text. Braces cannot be escaped.
/// </summary>
public sealed class TextTemplate
{
    // Each part is a literal string or a ParsedExpression, in the order of the text.
    private readonly ImmutableArray<object> _parts;

    private TextTemplate(string text, ImmutableArray<object> parts, ImmutableArray<ParsedExpression> expressions)
    {
        Text = text;
        _parts = parts;
        Expressions = expressions;
    }

    /// <summary>The text the template was parsed from.</summary>
    public string Text { get; }

    /// <summary>The expressions of the template, in the order of the text.</summary>
    public ImmutableArray<ParsedExpression> Expressions { get; }

    /// <summary>
    /// Parses the text with the variables of <see cref="ExpressionVariables"/>. On failure the error is that of the first
    /// expression that does not parse, with the position inside its braces, or <c>'{{' is not closed at position &lt;n&gt;</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="pathSeparator">The character between the segments of the paths of its expressions.</param>
    /// <param name="template">The template, or null.</param>
    /// <param name="error">The error, or null.</param>
    /// <exception cref="ArgumentException">The separator cannot be used in an expression, see <see cref="ParsedExpression.TryParse(string, char, out ParsedExpression?, out string?)"/>.</exception>
    public static bool TryParse(
        string text,
        char pathSeparator,
        [NotNullWhen(true)] out TextTemplate? template,
        [NotNullWhen(false)] out string? error)
    {
        ParsedExpression.ValidatePathSeparator(pathSeparator);
        return TryParseValidated(text, pathSeparator, ExpressionVariables.Default, out template, out error);
    }

    /// <summary>
    /// Parses the text with the variables the host declares, which replace those of <see cref="ExpressionVariables"/>. On
    /// failure the error is that of the first expression that does not parse, with the position inside its braces, or
    /// <c>'{{' is not closed at position &lt;n&gt;</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="pathSeparator">The character between the segments of the paths of its expressions.</param>
    /// <param name="variables">The names that are variables of its expressions, see <see cref="ParsedExpression.Variables"/>.</param>
    /// <param name="template">The template, or null.</param>
    /// <param name="error">The error, or null.</param>
    /// <exception cref="ArgumentException">The separator cannot be used in an expression, or a variable is no word an
    /// expression can hold, see <see cref="ParsedExpression.TryParse(string, char, ImmutableArray{string}, out ParsedExpression?, out string?)"/>.</exception>
    public static bool TryParse(
        string text,
        char pathSeparator,
        ImmutableArray<string> variables,
        [NotNullWhen(true)] out TextTemplate? template,
        [NotNullWhen(false)] out string? error)
    {
        ParsedExpression.ValidatePathSeparator(pathSeparator);
        ExpressionVariables.Validate(variables, pathSeparator);
        return TryParseValidated(text, pathSeparator, variables, out template, out error);
    }

    private static bool TryParseValidated(
        string text,
        char pathSeparator,
        ImmutableArray<string> variables,
        [NotNullWhen(true)] out TextTemplate? template,
        [NotNullWhen(false)] out string? error)
    {
        var parts = ImmutableArray.CreateBuilder<object>();
        var expressions = ImmutableArray.CreateBuilder<ParsedExpression>();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                parts.Add(text[position..]);
                break;
            }

            if (open > position)
            {
                parts.Add(text[position..open]);
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                template = null;
                error = $"'{{{{' is not closed at position {open}";
                return false;
            }

            if (!ParsedExpression.TryParseValidated(text[(open + 2)..close].Trim(), pathSeparator, variables, out var expression, out error))
            {
                template = null;
                return false;
            }

            parts.Add(expression);
            expressions.Add(expression);
            position = close + 2;
        }

        template = new TextTemplate(text, parts.ToImmutable(), expressions.ToImmutable());
        error = null;
        return true;
    }

    /// <summary>Renders the text with the text of each expression as the context writes it.</summary>
    public string Render<TContext>(ref TContext context)
        where TContext : struct, IExpressionContext
    {
        switch (_parts.Length)
        {
            case 0:
                return string.Empty;

            case 1:
                return _parts[0] as string ?? context.ToText(((ParsedExpression)_parts[0]).Evaluate(ref context));
        }

        // Rents its buffer, so that only the resulting text is allocated.
        var builder = new DefaultInterpolatedStringHandler(0, _parts.Length);
        foreach (var part in _parts)
        {
            builder.AppendLiteral(part as string ?? context.ToText(((ParsedExpression)part).Evaluate(ref context)));
        }

        return builder.ToStringAndClear();
    }
}
