using HomeBlaze.Services;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// The evaluation of automation expressions for one rule at one instant: paths resolve from the rule, <c>now</c> and
/// <c>time</c> are fixed, <c>event</c> and <c>subject</c> are the captured event, and the paths that led to no value
/// are collected.
/// </summary>
internal sealed class AutomationEvaluation
{
    private List<string>? _missingPaths;
    private readonly TimeZoneInfo _timeZone;
    private TimeSpan? _timeOfDay;

    public AutomationEvaluation(
        SubjectPathResolver resolver,
        AutomationRule rule,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        CapturedEvent? capturedEvent = null,
        ObservedValues? values = null)
    {
        Resolver = resolver;
        Rule = rule;
        Now = now;
        _timeZone = timeZone;
        Event = capturedEvent;
        Values = values;
    }

    public SubjectPathResolver Resolver { get; }

    public AutomationRule Rule { get; }

    public DateTimeOffset Now { get; }

    /// <summary>The local time of day of <see cref="Now"/>, as <see cref="LocalClock.GetTimeOfDay"/> reads it.</summary>
    public TimeSpan TimeOfDay => _timeOfDay ??= LocalClock.GetTimeOfDay(Now, _timeZone);

    public CapturedEvent? Event { get; }

    /// <summary>The observed values that paths read instead of the graph, or null to read the graph.</summary>
    public ObservedValues? Values { get; }

    /// <summary>The distinct paths of the last evaluation that led to no value, in order of first appearance.</summary>
    public IReadOnlyList<string> MissingPaths => _missingPaths ?? (IReadOnlyList<string>)[];

    public bool? EvaluateCondition(ParsedExpression expression)
    {
        _missingPaths?.Clear();
        var context = new AutomationExpressionContext(this);
        return expression.EvaluateCondition(ref context);
    }

    public ExpressionValue Evaluate(ParsedExpression expression)
    {
        _missingPaths?.Clear();
        var context = new AutomationExpressionContext(this);
        return expression.Evaluate(ref context);
    }

    public string Render(TextTemplate template)
    {
        _missingPaths?.Clear();
        var context = new AutomationExpressionContext(this);
        return template.Render(ref context);
    }

    internal void AddMissingPath(string path)
    {
        _missingPaths ??= [];
        if (!_missingPaths.Contains(path))
        {
            _missingPaths.Add(path);
        }
    }
}
