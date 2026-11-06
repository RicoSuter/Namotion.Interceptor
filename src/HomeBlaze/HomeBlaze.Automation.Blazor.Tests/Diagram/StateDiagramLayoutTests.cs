using HomeBlaze.Automation.Blazor.Diagram;

namespace HomeBlaze.Automation.Blazor.Tests.Diagram;

public class StateDiagramLayoutTests
{
    private static StateDiagramRule Rule(string key, string? inState, params AutomationAction[] actions) => new(key, key, inState, actions);

    private static ChangeStateAction Enter(string state) => new() { Id = "state-1", TargetState = state };

    private static InvokeOperationAction Notify() => new() { Id = "call-1", OperationPath = "/Notify/Send" };

    [Fact]
    public void WhenRulesChangeTheStateBackAndForth_ThenTheirEdgesArcAboveAndBelowTheRow()
    {
        // Act
        var layout = StateDiagramLayout.Create(
            ["Idle", "Running"],
            "Idle",
            "Running",
            [Rule("started", "Idle", Enter("Running")), Rule("finished", "Running", Notify(), Enter("Idle")), Rule("offline", null, Notify())]);

        // Assert
        var idle = layout.Nodes[0];
        var running = layout.Nodes[1];
        Assert.Equal(("Idle", true, false), (idle.State, idle.IsInitial, idle.IsCurrent));
        Assert.Equal(("Running", false, true), (running.State, running.IsInitial, running.IsCurrent));
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
        var layout = StateDiagramLayout.Create(["Closed", "Open"], "Closed", null, [Rule("shut", null, Enter("Closed"))]);

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
            null,
            [Rule("twice", "A", Enter("B"), Enter("A")), Rule("unknown", "A", Enter("C")), Rule("same", "A", Enter("A")), Rule("outside", "C", Enter("B"))]);

        // Assert
        Assert.Empty(layout.Edges);
        Assert.Equal(["twice", "unknown", "same", "outside"], layout.OtherRules.Select(rule => rule.Key).ToArray());
    }

    [Fact]
    public void WhenTwoRulesConnectTheSameStates_ThenTheirArcsHaveDifferentHeights()
    {
        // Act
        var layout = StateDiagramLayout.Create(["A", "B"], "A", null, [Rule("first", "A", Enter("B")), Rule("second", "A", Enter("B"))]);

        // Assert
        Assert.NotEqual(layout.Edges[0].LabelY, layout.Edges[1].LabelY);
        Assert.NotEqual(layout.Edges[0].PathData, layout.Edges[1].PathData);
    }

    [Fact]
    public void WhenTwoRulesWithDetailsConnectTheSameStates_ThenTheirLabelsDoNotOverlap()
    {
        // Act
        var layout = StateDiagramLayout.Create(
            ["A", "B"], "A", null, [Rule("first", "A", Notify(), Enter("B")), Rule("second", "A", Notify(), Enter("B")), Rule("back", "B", Notify(), Enter("A"))]);

        // Assert
        var labelsAbove = layout.Edges.Where(edge => edge.From == "A").Select(edge => edge.LabelY).Order().ToArray();
        Assert.True(labelsAbove[1] - labelsAbove[0] >= 30);
        Assert.True(layout.Edges.All(edge => edge.LabelY - 14 > 0));
        Assert.True(layout.Edges.Where(edge => edge.From == "A").All(edge => edge.LabelY + 14 < layout.Nodes[0].Y));
        Assert.True(layout.Edges.Single(edge => edge.From == "B").LabelY > layout.Nodes[0].Y + StateDiagramLayout.NodeHeight);
    }
}
