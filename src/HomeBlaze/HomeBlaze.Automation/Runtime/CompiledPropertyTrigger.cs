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
        Clock = ExpressionClock.Combine(ExpressionClock.Create(expression), rearm is null ? null : ExpressionClock.Create(rearm));
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

    /// <summary>When the expressions can change with the clock alone, or null for a trigger that does not read the clock.</summary>
    public ExpressionClock? Clock { get; }

    /// <summary>
    /// When the trigger is evaluated again without a change of its properties: when its hold ends or its expressions can
    /// change with the clock, whichever comes first; null for neither.
    /// </summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    public PropertyTriggerState State { get; private set; }

    /// <summary>The properties that <see cref="PropertyPaths"/> resolved to when the trigger was last bound.</summary>
    public PropertyReference[] Dependencies { get; set; } = [];

    /// <summary>The graph route of <see cref="PropertyPaths"/> when the trigger was last bound.</summary>
    public GraphRoute Route { get; set; } = GraphRoute.Empty;

    /// <summary>The structure version that <see cref="Route"/> was recorded at, see <see cref="GraphRouteBuilder.ResolvedAt"/>.</summary>
    public long RouteVersion { get; set; } = -1;

    public string Description => $"trigger {Index + 1} ({Configuration.Expression})";

    /// <summary>Hands the state to the trigger that replaces this one with an equal configuration, and starts over.</summary>
    public void MoveStateTo(CompiledPropertyTrigger target)
    {
        target.State = State;
        target.Dependencies = Dependencies;
        target.Route = Route;
        target.RouteVersion = RouteVersion;
        target.NextCheckAt = NextCheckAt;
        State = CreateState();
        Dependencies = [];
        Route = GraphRoute.Empty;
        RouteVersion = -1;
    }

    private PropertyTriggerState CreateState() => new(Configuration.For, Rearm is not null);
}
