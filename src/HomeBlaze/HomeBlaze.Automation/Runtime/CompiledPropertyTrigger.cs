using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>A property trigger with its parsed expressions, the properties it reads and its state.</summary>
internal sealed class CompiledPropertyTrigger
{
    public CompiledPropertyTrigger(CompiledRule rule, int index, PropertyTrigger configuration, ParsedExpression expression, ParsedExpression? rearm)
    {
        Rule = rule;
        Index = index;
        Configuration = configuration;
        Expression = expression;
        Rearm = rearm;
        var rearmPaths = rearm is null ? Enumerable.Empty<string>() : AutomationExpressionCheck.GetPropertyPaths(rearm);
        PropertyPaths = [.. AutomationExpressionCheck.GetPropertyPaths(expression).Concat(rearmPaths).Distinct()];
        UsesTime = AutomationExpressionCheck.UsesTime(expression) || (rearm is not null && AutomationExpressionCheck.UsesTime(rearm));
        State = CreateState();
    }

    public CompiledRule Rule { get; }

    /// <summary>The position of the trigger among all triggers of its rule.</summary>
    public int Index { get; }

    public PropertyTrigger Configuration { get; }

    public ParsedExpression Expression { get; }

    public ParsedExpression? Rearm { get; }

    /// <summary>The paths of the expression and the rearm expression that are no variable.</summary>
    public IReadOnlyList<string> PropertyPaths { get; }

    /// <summary>Whether the trigger reads <c>now</c> or <c>time</c> and is evaluated every second.</summary>
    public bool UsesTime { get; }

    public PropertyTriggerState State { get; private set; }

    /// <summary>The properties that <see cref="PropertyPaths"/> resolved to when the trigger was last bound.</summary>
    public PropertyReference[] Dependencies { get; set; } = [];

    public string Description => $"trigger {Index + 1} ({Configuration.Expression})";

    /// <summary>Hands the state to the trigger that replaces this one with an equal configuration, and starts over.</summary>
    public void MoveStateTo(CompiledPropertyTrigger target)
    {
        target.State = State;
        target.Dependencies = Dependencies;
        State = CreateState();
        Dependencies = [];
    }

    private PropertyTriggerState CreateState() => new(Configuration.For, Rearm is not null);
}
