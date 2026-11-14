using System.Globalization;
using HomeBlaze.Automation.Blazor.Diagram;

namespace HomeBlaze.Automation.Blazor.Tests.Diagram;

public class StateDiagramLayoutTests
{
    private static StateDiagramRule Rule(string key, string? inState, params AutomationAction[] actions) =>
        StateDiagramRule.From(key, new AutomationRule { InState = inState, Actions = actions });

    private static ChangeStateAction Enter(string state) => new() { Id = "state-1", TargetState = state };

    private static InvokeOperationAction Notify() => new() { Id = "call-1", OperationPath = "/Notify/Send" };

    [Fact]
    public void WhenRulesChangeTheStateBackAndForth_ThenTheirEdgesArcAboveAndBelowTheRow()
    {
        // Act
        var layout = StateDiagramLayout.Create(
            ["Idle", "Running"],
            "Idle",
            [Rule("started", "Idle", Enter("Running")), Rule("finished", "Running", Notify(), Enter("Idle")), Rule("offline", null, Notify())]);

        // Assert
        var idle = layout.Nodes[0];
        var running = layout.Nodes[1];
        Assert.Equal(("Idle", true), (idle.State, idle.IsInitial));
        Assert.Equal(("Running", false), (running.State, running.IsInitial));
        var started = layout.Edges.Single(edge => edge.RuleKey == "started");
        var finished = layout.Edges.Single(edge => edge.RuleKey == "finished");
        Assert.Equal(("Idle", "Running"), (started.From, started.To));
        Assert.True(started.LabelY < idle.Y);
        Assert.True(finished.LabelY > idle.Y + StateDiagramLayout.NodeHeight);
        Assert.Null(started.Detail);
        Assert.Equal("and 1 more action", finished.Detail);
        Assert.Equal(["offline"], layout.OtherRules.Select(rule => rule.Key).ToArray());
        Assert.Equal((16 * 2) + (2 * StateDiagramLayout.NodeWidth) + 110, layout.Width);
        Assert.True(layout.Height > idle.Y + StateDiagramLayout.NodeHeight);
    }

    [Fact]
    public void WhenARuleChangesTheStateFromAnyState_ThenItsEdgeStartsAtAnAnyStateNode()
    {
        // Act
        var layout = StateDiagramLayout.Create(["Closed", "Open"], "Closed", [Rule("shut", null, Enter("Closed"))]);

        // Assert
        Assert.Null(layout.Nodes[0].State);
        Assert.Equal(["Closed", "Open"], layout.Nodes.Skip(1).Select(node => node.State!).ToArray());
        var edge = Assert.Single(layout.Edges);
        Assert.Null(edge.From);
        Assert.Equal("Closed", edge.To);
    }

    [Fact]
    public void WhenARuleHasNoSingleSourceAndTarget_ThenItIsNoEdge()
    {
        // Act
        var layout = StateDiagramLayout.Create(
            ["A", "B"],
            "A",
            [Rule("twice", "A", Enter("B"), Enter("A")), Rule("unknown", "A", Enter("C")), Rule("same", "A", Enter("A")), Rule("outside", "C", Enter("B"))]);

        // Assert
        Assert.Empty(layout.Edges);
        Assert.Equal(["twice", "unknown", "same", "outside"], layout.OtherRules.Select(rule => rule.Key).ToArray());
    }

    [Fact]
    public void WhenTwoRulesConnectTheSameStates_ThenTheirArcsHaveDifferentHeights()
    {
        // Act
        var layout = StateDiagramLayout.Create(["A", "B"], "A", [Rule("first", "A", Enter("B")), Rule("second", "A", Enter("B"))]);

        // Assert
        Assert.NotEqual(layout.Edges[0].LabelY, layout.Edges[1].LabelY);
        Assert.NotEqual(layout.Edges[0].PathData, layout.Edges[1].PathData);
    }

    [Fact]
    public void WhenTwoRulesWithDetailsConnectTheSameStates_ThenTheirLabelsDoNotOverlap()
    {
        // Act
        var layout = StateDiagramLayout.Create(
            ["A", "B"], "A", [Rule("first", "A", Notify(), Enter("B")), Rule("second", "A", Notify(), Enter("B")), Rule("back", "B", Notify(), Enter("A"))]);

        // Assert
        var labelsAbove = layout.Edges.Where(edge => edge.From == "A").Select(edge => edge.LabelY).Order().ToArray();
        Assert.True(labelsAbove[1] - labelsAbove[0] >= 30);
        Assert.True(layout.Edges.All(edge => edge.LabelY - 14 > 0));
        Assert.True(layout.Edges.Where(edge => edge.From == "A").All(edge => edge.LabelY + 14 < layout.Nodes[0].Y));
        Assert.True(layout.Edges.Single(edge => edge.From == "B").LabelY > layout.Nodes[0].Y + StateDiagramLayout.NodeHeight);
    }

    [Fact]
    public void WhenRulesParallelAndCrossTheRow_ThenNoArrowRunsThroughALabel()
    {
        // Arrange
        string[] states = ["S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8"];
        var rules = new List<StateDiagramRule>();
        for (var index = 0; index < states.Length; index++)
        {
            var next = states[(index + 1) % states.Length];
            rules.Add(Rule($"advance-{index}", states[index], Notify(), Enter(next)));
            rules.Add(Rule($"skip-{index}", states[index], Enter(states[(index + 2) % states.Length])));
        }

        rules.Add(Rule("again", "S1", Notify(), Enter("S2")));
        rules.Add(Rule("reset", null, Enter("S1")));

        // Act
        var layout = StateDiagramLayout.Create(states, "S1", rules);

        // Assert
        Assert.Empty(layout.OtherRules);
        var labels = layout.Edges.Select(edge => (Edge: edge, Box: GetLabelBox(edge))).ToList();
        foreach (var edge in layout.Edges)
        {
            foreach (var point in Sample(edge.PathData))
            {
                var hit = labels.Find(label => point.X > label.Box.Left && point.X < label.Box.Right && point.Y > label.Box.Top && point.Y < label.Box.Bottom);
                Assert.True(hit.Edge is null, $"The arrow of {edge.RuleKey} runs through the label of {hit.Edge?.RuleKey}");
            }
        }

        foreach (var node in layout.Nodes)
        {
            Assert.DoesNotContain(labels, label => label.Box.Left < node.X + StateDiagramLayout.NodeWidth && node.X < label.Box.Right
                && label.Box.Top < node.Y + StateDiagramLayout.NodeHeight && node.Y < label.Box.Bottom);
        }
    }

    [Fact]
    public void WhenEveryStateChangesToEveryOther_ThenTheDiagramDrawsAtMostItsLimitAndListsTheOtherRules()
    {
        // Arrange
        var states = Enumerable.Range(1, 16).Select(number => $"S{number}").ToArray();
        var rules = states.SelectMany(from => states.Where(to => to != from).Select(to => Rule($"{from}-{to}", from, Enter(to)))).ToList();

        // Act
        var layout = StateDiagramLayout.Create(states, "S1", rules);

        // Assert
        Assert.Equal(StateDiagramLayout.MaxEdges, layout.Edges.Count);
        Assert.Equal(rules.Count - StateDiagramLayout.MaxEdges, layout.OtherRules.Count);
        Assert.Equal(rules[0].Key, layout.Edges[0].RuleKey);
    }

    private static (double Left, double Right, double Top, double Bottom) GetLabelBox(StateDiagramEdge edge)
    {
        var width = Math.Max(edge.Label.Length * 7.0, (edge.Detail?.Length ?? 0) * 6.0);
        return (edge.LabelX - (width / 2), edge.LabelX + (width / 2), edge.LabelY - 11, edge.LabelY + (edge.Detail is null ? 3 : 17));
    }

    private static IEnumerable<(double X, double Y)> Sample(string pathData)
    {
        var parts = pathData.Split(' ');
        var numbers = new[] { 1, 2, 4, 5, 6, 7, 8, 9 }.Select(index => double.Parse(parts[index], CultureInfo.InvariantCulture)).ToArray();
        for (var step = 0; step <= 40; step++)
        {
            var t = step / 40.0;
            var (a, b, c, d) = ((1 - t) * (1 - t) * (1 - t), 3 * t * (1 - t) * (1 - t), 3 * t * t * (1 - t), t * t * t);
            yield return (
                (a * numbers[0]) + (b * numbers[2]) + (c * numbers[4]) + (d * numbers[6]),
                (a * numbers[1]) + (b * numbers[3]) + (c * numbers[5]) + (d * numbers[7]));
        }
    }
}
