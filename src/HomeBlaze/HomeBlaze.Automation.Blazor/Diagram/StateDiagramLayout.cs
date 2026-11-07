using System.Globalization;

namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>
/// The picture of the states of an automation and the rules between them: one row of states, preceded by an "Any state"
/// node when a rule without a state scope changes the state. A rule with exactly one state change, from the state it is
/// active in to another state, is an edge, up to <see cref="MaxEdges"/>; an edge to the right arcs above the row and one to the left below it, as high as it takes to clear the labels and arrows below it. Every other rule is listed in <see cref="OtherRules"/>, so the rule list stays the only store
/// of transitions. The positions are computed and never stored, and they do not depend on the current state.
/// </summary>
public sealed class StateDiagramLayout
{
    public const double NodeWidth = 150;
    public const double NodeHeight = 64;

    /// <summary>The most edges the diagram draws; the rules after them are listed in <see cref="OtherRules"/>.</summary>
    public const int MaxEdges = 40;

    private const double Margin = 16;
    private const double Gap = 110;
    private const double BaseRise = 44;
    private const int MaxLabelLength = 24;

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

    public static StateDiagramLayout Create(IReadOnlyList<string> states, string? initialState, IReadOnlyList<StateDiagramRule> rules)
    {
        var transitions = new List<Transition>();
        var others = new List<StateDiagramRule>();
        foreach (var rule in rules)
        {
            if (transitions.Count < MaxEdges && GetTransition(rule, states) is { } transition)
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
        var arcs = transitions.Select((transition, index) => CreateArc(transition, index, columns)).ToList();
        var placed = Place(arcs);
        var rowTop = Margin + placed.Where(arc => arc.Arc.IsAbove).Select(arc => arc.LabelExtent).DefaultIfEmpty(0).Max();
        var spaceBelow = placed.Where(arc => !arc.Arc.IsAbove).Select(arc => arc.LabelExtent).DefaultIfEmpty(0).Max();
        var nodes = columns
            .Select((state, column) => new StateDiagramNode(state, state is not null && state == initialState, ColumnX(column), rowTop))
            .ToList();
        var edges = placed.OrderBy(arc => arc.Arc.Index).Select(arc => CreateEdge(arc, rowTop)).ToList();
        var width = (Margin * 2) + (columns.Count * NodeWidth) + (Math.Max(columns.Count - 1, 0) * Gap);
        return new StateDiagramLayout(nodes, edges, others, width, rowTop + NodeHeight + spaceBelow + Margin);
    }

    private static double ColumnX(int column) => Margin + (column * (NodeWidth + Gap));

    private static Transition? GetTransition(StateDiagramRule rule, IReadOnlyList<string> states)
    {
        if (rule.TargetState is not { } target || !states.Contains(target))
        {
            return null;
        }

        if (rule.InState is null)
        {
            return new Transition(rule, null, target);
        }

        return states.Contains(rule.InState) && rule.InState != target ? new Transition(rule, rule.InState, target) : null;
    }

    private static Arc CreateArc(Transition transition, int index, List<string?> columns)
    {
        var rule = transition.Rule;
        var label = rule.Name.Length > MaxLabelLength ? rule.Name[..(MaxLabelLength - 1)] + "\u2026" : rule.Name;
        var detail = DescribeOtherActions(rule);
        var labelWidth = Math.Max(label.Length * 7.0, (detail?.Length ?? 0) * 6.0) + 8;
        return new Arc(index, transition, columns.IndexOf(transition.From), columns.IndexOf(transition.To), label, detail, labelWidth);
    }

    private static List<PlacedArc> Place(List<Arc> arcs)
    {
        var anchors = AssignAnchors(arcs);
        var above = new Lane(isAbove: true);
        var below = new Lane(isAbove: false);
        var placed = new List<PlacedArc>();
        foreach (var arc in arcs.OrderBy(arc => arc.Span).ThenBy(arc => arc.Index))
        {
            var startX = anchors[(arc.Index, true)];
            var endX = anchors[(arc.Index, false)];
            var rise = (arc.IsAbove ? above : below).Fit(startX, endX, arc.LabelWidth);
            placed.Add(new PlacedArc(arc, startX, endX, rise));
        }

        return placed;
    }

    // Arrows that leave or enter the same side of a state get their own anchor, ordered so that the arcs nest instead of crossing.
    private static Dictionary<(int Index, bool IsStart), double> AssignAnchors(List<Arc> arcs)
    {
        var anchors = new Dictionary<(int Index, bool IsStart), double>();
        var groups = arcs
            .SelectMany(arc => new[] { (Arc: arc, IsStart: true, Column: arc.FromColumn), (Arc: arc, IsStart: false, Column: arc.ToColumn) })
            .GroupBy(item => (item.Column, item.Arc.IsAbove, item.IsStart));
        foreach (var group in groups)
        {
            var (column, isAbove, isStart) = group.Key;
            var members = group.OrderBy(item => item.Arc.Span).ThenBy(item => item.Arc.Index).ToList();
            if (isAbove == isStart)
            {
                members.Reverse();
            }

            var onRight = isAbove == isStart;
            var low = onRight ? 0.5 : 0.1;
            var high = onRight ? 0.9 : 0.5;
            for (var slot = 0; slot < members.Count; slot++)
            {
                var fraction = low + ((high - low) * (slot + 1) / (members.Count + 1));
                anchors[(members[slot].Arc.Index, isStart)] = ColumnX(column) + (NodeWidth * fraction);
            }
        }

        return anchors;
    }

    private static StateDiagramEdge CreateEdge(PlacedArc placed, double rowTop)
    {
        var arc = placed.Arc;
        var edgeY = arc.IsAbove ? rowTop : rowTop + NodeHeight;
        var direction = arc.IsAbove ? -1 : 1;
        var middleX = (placed.StartX + placed.EndX) / 2;
        var controlY = edgeY + (direction * placed.Rise);
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"M {placed.StartX:0.#} {edgeY:0.#} C {placed.StartX:0.#} {controlY:0.#} {placed.EndX:0.#} {controlY:0.#} {placed.EndX:0.#} {edgeY:0.#}");
        var labelY = edgeY + (direction * (Lane.GetPeak(placed.Rise) + Lane.LabelOffset(arc.IsAbove)));
        var rule = arc.Transition.Rule;
        return new StateDiagramEdge(rule.Key, arc.Label, arc.Detail, arc.Transition.From, arc.Transition.To, path, middleX, labelY, rule.Name);
    }

    private static string? DescribeOtherActions(StateDiagramRule rule) => (rule.ActionCount - 1) switch
    {
        <= 0 => null,
        1 => "and 1 more action",
        var count => $"and {count} more actions"
    };

    private sealed record Transition(StateDiagramRule Rule, string? From, string To);

    private sealed record Arc(int Index, Transition Transition, int FromColumn, int ToColumn, string Label, string? Detail, double LabelWidth)
    {
        public bool IsAbove => FromColumn < ToColumn;

        public int Span => Math.Abs(ToColumn - FromColumn);
    }

    private sealed record PlacedArc(Arc Arc, double StartX, double EndX, double Rise)
    {
        public double LabelExtent => Lane.GetPeak(Rise) + Lane.LabelTop(Arc.IsAbove);
    }

    private readonly record struct Box(double Left, double Right, double Bottom, double Top)
    {
        public bool Contains(double x, double height, double padding) =>
            x >= Left - padding && x <= Right + padding && height >= Bottom - padding && height <= Top + padding;

        public bool Overlaps(Box other) =>
            Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;
    }

    /// <summary>The arcs of one side of the row, as sampled curves and label boxes in distance from the row.</summary>
    private sealed class Lane(bool isAbove)
    {
        private const double RiseStep = 8;

        // An arc that clears nothing below this height is drawn at it, over the others, so that a dense diagram stays cheap.
        private const int MaxRiseSteps = 64;
        private const int Samples = 48;
        private const double Clearance = 3;
        private const double ArrowClearance = 10;

        private readonly List<(double X, double Height)[]> _curves = [];
        private readonly List<Box> _labels = [];

        // A cubic curve whose control points sit above its ends reaches three quarters of the control height in the middle.
        public static double GetPeak(double rise) => rise * 0.75;

        public static double LabelOffset(bool isAbove) => isAbove ? 22 : 18;

        public static double LabelTop(bool isAbove) => isAbove ? 34 : 36;

        public double Fit(double startX, double endX, double labelWidth)
        {
            var curve = new (double X, double Height)[Samples + 1];
            double rise;
            Box label;
            var step = 0;
            do
            {
                rise = BaseRise + (step * RiseStep);
                Sample(curve, startX, endX, rise);
                label = CreateLabelBox((startX + endX) / 2, labelWidth, rise);
                step++;
            }
            while (step <= MaxRiseSteps && Conflicts(curve, label));

            _curves.Add(curve);
            _labels.Add(label);
            return rise;
        }

        private static void Sample((double X, double Height)[] points, double startX, double endX, double rise)
        {
            for (var index = 0; index <= Samples; index++)
            {
                var t = (double)index / Samples;
                points[index] = (startX + ((endX - startX) * t * t * (3 - (2 * t))), 3 * t * (1 - t) * rise);
            }
        }

        private Box CreateLabelBox(double middleX, double labelWidth, double rise)
        {
            var peak = GetPeak(rise);
            return new Box(middleX - (labelWidth / 2), middleX + (labelWidth / 2), peak + (isAbove ? 4 : 6), peak + LabelTop(isAbove));
        }

        private bool Conflicts(IReadOnlyList<(double X, double Height)> curve, Box label) =>
            _labels.Exists(other => other.Overlaps(label))
            || curve.Any(point => _labels.Exists(other => other.Contains(point.X, point.Height, Clearance)))
            || _curves.Exists(other => other.Any(point => label.Contains(point.X, point.Height, Clearance)))
            || _curves.Exists(other => Touches(curve, other));

        private static bool Touches(IReadOnlyList<(double X, double Height)> curve, (double X, double Height)[] other) =>
            curve.Any(point => point.Height > 2 * ArrowClearance && Array.Exists(other, candidate => Math.Abs(candidate.X - point.X) < 8 && Math.Abs(candidate.Height - point.Height) < ArrowClearance));
    }
}
