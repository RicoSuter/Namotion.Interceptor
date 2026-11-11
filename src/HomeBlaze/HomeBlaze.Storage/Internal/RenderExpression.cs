using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using Namotion.Interceptor.Expressions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Shows the value of an expression in the text of a page. A path with a / prefix resolves from the root. Every other path
/// resolves from the page, and its first segment can be the name of a block on that page.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
public partial class RenderExpression : ITitleProvider
{
    private readonly SubjectPathResolver _pathResolver;
    private readonly ParsedExpression? _expression;

    /// <summary>The text between the braces, as the page has it.</summary>
    public string Text { get; }

    /// <summary>What is wrong with the expression as written, or null. It is shown in place of the value.</summary>
    public string? Error { get; }

    public MarkdownFile Parent { get; }

    public string? Title => null;

    // No production code reads this: a render reads the text through GetText, which also reads the version of the paths,
    // so it renders again when the target of an absolute path appears. Its change notification tells other observers when
    // the value changes, but not when a target appears. It is the value before the pipes format it.
    [Derived]
    public object? Value
    {
        get
        {
            if (_expression is null)
            {
                return null;
            }

            // Without the read check: the derived value is computed again on the flow of whoever changes a property it
            // reads, such as an agent whose check refuses it, and would then miss the value and its dependency for everyone.
            var context = new MarkdownExpressionContext(_pathResolver, Parent, timeZone: null, checksReadAccess: false);
            return _expression.EvaluateWithoutPipes(ref context).Value;
        }
    }

    public RenderExpression(string text, MarkdownFile parent, SubjectPathResolver pathResolver)
    {
        Text = text;
        Parent = parent;
        _pathResolver = pathResolver;
        _expression = MarkdownExpressions.Parse(text, out var error);
        Error = error;
    }

    /// <summary>
    /// Gets the value as the page shows it. A missing value, also one of a property that <see cref="PropertyReadAccess"/>
    /// refuses on the current flow, gives an empty text.
    /// </summary>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for the time zone of the server.</param>
    public string GetText(ITimeZoneDisplay? timeZone)
    {
        if (_expression is null)
        {
            return string.Empty;
        }

        var context = new MarkdownExpressionContext(_pathResolver, Parent, timeZone);
        return context.ToText(_expression.Evaluate(ref context));
    }
}
