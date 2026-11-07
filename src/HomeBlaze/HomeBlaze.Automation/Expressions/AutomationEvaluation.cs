using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// The evaluation of automation expressions for one rule at one instant: paths resolve from the rule, <c>now</c> and
/// <c>time</c> are fixed, <c>event</c> and <c>subject</c> are the captured event, and the paths that led to no value
/// are collected.
/// </summary>
internal sealed class AutomationEvaluation
{
    private readonly List<string> _missingPaths = [];
    private readonly Func<RegisteredSubjectProperty, bool>? _canRead;

    /// <param name="resolver">Resolves the paths of expressions.</param>
    /// <param name="rule">The rule that relative paths start at.</param>
    /// <param name="now">The instant of the evaluation.</param>
    /// <param name="timeZone">The zone that <c>time</c> is read in.</param>
    /// <param name="capturedEvent">The event behind <c>event</c> and <c>subject</c>, or null.</param>
    /// <param name="values">The observed values that paths read instead of the graph, or null to read the graph.</param>
    /// <param name="canRead">
    /// Whether the caller may read a property, for evaluations shown to a caller; a property it refuses is missing. Null,
    /// as for the runtime, reads every property an expression may read.
    /// </param>
    public AutomationEvaluation(
        SubjectPathResolver resolver,
        IInterceptorSubject rule,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        CapturedEvent? capturedEvent = null,
        ObservedValues? values = null,
        Func<RegisteredSubjectProperty, bool>? canRead = null)
    {
        Resolver = resolver;
        Rule = rule;
        Now = now;
        TimeOfDay = TimeZoneInfo.ConvertTime(now, timeZone).TimeOfDay;
        Event = capturedEvent;
        Values = values;
        _canRead = canRead;
    }

    public SubjectPathResolver Resolver { get; }

    public IInterceptorSubject Rule { get; }

    public DateTimeOffset Now { get; }

    public TimeSpan TimeOfDay { get; }

    public CapturedEvent? Event { get; }

    /// <summary>The observed values that paths read instead of the graph, or null to read the graph.</summary>
    public ObservedValues? Values { get; }

    /// <summary>The distinct paths of the last evaluation that led to no value, in order of first appearance.</summary>
    public IReadOnlyList<string> MissingPaths => _missingPaths;

    /// <summary>Gets whether the caller of the evaluation may read the property.</summary>
    public bool CanRead(RegisteredSubjectProperty property) => _canRead?.Invoke(property) ?? true;

    public bool? EvaluateCondition(ParsedExpression expression)
    {
        _missingPaths.Clear();
        var context = new AutomationExpressionContext(this);
        return expression.EvaluateCondition(ref context);
    }

    public ExpressionValue Evaluate(ParsedExpression expression)
    {
        _missingPaths.Clear();
        var context = new AutomationExpressionContext(this);
        return expression.Evaluate(ref context);
    }

    public string Render(TextTemplate template)
    {
        _missingPaths.Clear();
        var context = new AutomationExpressionContext(this);
        return template.Render(ref context);
    }

    internal void AddMissingPath(string path)
    {
        if (!_missingPaths.Contains(path))
        {
            _missingPaths.Add(path);
        }
    }
}
