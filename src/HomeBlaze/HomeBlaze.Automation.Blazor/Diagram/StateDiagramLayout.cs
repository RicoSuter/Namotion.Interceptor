using System.Globalization;

namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>
/// The picture of the states of an automation and the rules between them: one row of states, preceded by an "Any state"
/// node when a rule without a state scope changes the state. A rule with exactly one state change, from the state it is
/// active in to another state, is an edge; an edge to the right arcs above the row and one to the left below it, higher
/// the farther it reaches. Every other rule is listed in <see cref="OtherRules"/>, so the rule list stays the only store
/// of transitions. The positions are computed and never stored.
/// </summary>
public sealed class StateDiagramLayout
{
    public const double NodeWidth = 150;
    public const double NodeHeight = 64;

    private const double Margin = 16;
    private const double Gap = 110;
    private const double BaseRise = 56;
    private const double RisePerColumn = 32;
    private const double RisePerParallelEdge = 64;
    private const double LabelSpace = 44;

    private StateDiagramLayout(
        IReadOnlyList<StateDiagramNode> nodes, IReadOnlyList<StateDiagramEdge> edges, IReadOnlyList<StateDiagramRule> otherRules, double width, double height)
    {
        Nodes = nodes;
        Edges = edges;
        OtherRules = otherRules;
        Width = width;
        Height = height;
    }

    public IReadOnlyList<StateDiagramNode> Nodes { get; }

    public IReadOnlyList<StateDiagramEdge> Edges { get; }

    /// <summary>The rules that are no edge: without a state change, with several, or with one the diagram cannot show.</summary>
    public IReadOnlyList<StateDiagramRule> OtherRules { get; }

    public double Width { get; }

    public double Height { get; }

    public static StateDiagramLayout Create(
        IReadOnlyList<string> states, string? initialState, string? currentState, IReadOnlyList<StateDiagramRule> rules)
    {
        var transitions = new List<Transition>();
        var others = new List<StateDiagramRule>();
        foreach (var rule in rules)
        {
            if (GetTransition(rule, states) is { } transition)
            {
                transitions.Add(transition);
            }
            else
            {
                others.Add(rule);
            }
        }

        var columns = new List<string?>();
        if (transitions.Exists(transition => transition.From is null))
        {
            columns.Add(null);
        }

        columns.AddRange(states);
        var arcs = transitions.Select((transition, index) => CreateArc(transition, columns, transitions.Take(index))).ToList();
        var rowTop = Margin + LabelSpace + arcs.Where(arc => arc.IsAbove).Select(arc => arc.Rise / 2).DefaultIfEmpty(0).Max();
        var spaceBelow = LabelSpace + arcs.Where(arc => !arc.IsAbove).Select(arc => arc.Rise / 2).DefaultIfEmpty(0).Max();
        var nodes = columns
            .Select((state, column) => new StateDiagramNode(state, state is not null && state == initialState, state is not null && state == currentState, ColumnX(column), rowTop))
            .ToList();
        var edges = arcs.Select(arc => CreateEdge(arc, rowTop)).ToList();
        var width = (Margin * 2) + (columns.Count * NodeWidth) + (Math.Max(columns.Count - 1, 0) * Gap);
        return new StateDiagramLayout(nodes, edges, others, width, rowTop + NodeHeight + spaceBelow + Margin);
    }

    private static double ColumnX(int column) => Margin + (column * (NodeWidth + Gap));

    private static Transition? GetTransition(StateDiagramRule rule, IReadOnlyList<string> states)
    {
        var targets = rule.Actions.OfType<ChangeStateAction>().Select(action => action.TargetState).ToList();
        if (targets.Count != 1 || !states.Contains(targets[0]))
        {
            return null;
        }

        var target = targets[0];
        if (rule.InState is null)
        {
            return new Transition(rule, null, target);
        }

        return states.Contains(rule.InState) && rule.InState != target ? new Transition(rule, rule.InState, target) : null;
    }

    private static Arc CreateArc(Transition transition, List<string?> columns, IEnumerable<Transition> earlier)
    {
        var fromColumn = columns.IndexOf(transition.From);
        var toColumn = columns.IndexOf(transition.To);
        var parallel = earlier.Count(other => other.From == transition.From && other.To == transition.To);
        var rise = BaseRise + (RisePerColumn * (Math.Abs(toColumn - fromColumn) - 1)) + (RisePerParallelEdge * parallel);
        return new Arc(transition, fromColumn, toColumn, rise);
    }

    private static StateDiagramEdge CreateEdge(Arc arc, double rowTop)
    {
        var isAbove = arc.IsAbove;
        var startX = ColumnX(arc.FromColumn) + (NodeWidth * (isAbove ? 0.65 : 0.35));
        var endX = ColumnX(arc.ToColumn) + (NodeWidth * (isAbove ? 0.35 : 0.65));
        var edgeY = isAbove ? rowTop : rowTop + NodeHeight;
        var controlY = isAbove ? edgeY - arc.Rise : edgeY + arc.Rise;
        var middleX = (startX + endX) / 2;

        // A quadratic curve passes halfway between its ends and its control point at its middle.
        var peakY = (edgeY + controlY) / 2;
        var labelY = isAbove ? peakY - 22 : peakY + 18;
        var path = string.Create(
            CultureInfo.InvariantCulture, $"M {startX:0.#} {edgeY:0.#} Q {middleX:0.#} {controlY:0.#} {endX:0.#} {edgeY:0.#}");
        var rule = arc.Transition.Rule;
        return new StateDiagramEdge(rule.Key, rule.Name, DescribeOtherActions(rule), arc.Transition.From, arc.Transition.To, path, middleX, labelY);
    }

    private static string? DescribeOtherActions(StateDiagramRule rule) => (rule.Actions.Count - 1) switch
    {
        <= 0 => null,
        1 => "and 1 more action",
        var count => $"and {count} more actions"
    };

    private sealed record Transition(StateDiagramRule Rule, string? From, string To);

    private sealed record Arc(Transition Transition, int FromColumn, int ToColumn, double Rise)
    {
        public bool IsAbove => FromColumn < ToColumn;
    }
}
