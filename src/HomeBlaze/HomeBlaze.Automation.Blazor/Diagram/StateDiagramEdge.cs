namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>A rule drawn as an arrow from its state to the state it changes to.</summary>
/// <param name="RuleKey">The key of the rule.</param>
/// <param name="Label">The name of the rule.</param>
/// <param name="Detail">The other actions of the rule, such as "and 1 more action", or null.</param>
/// <param name="From">The state the rule is active in, or null for any state.</param>
/// <param name="To">The state the rule changes to.</param>
/// <param name="PathData">The SVG path of the arrow.</param>
/// <param name="LabelX">The center of the label.</param>
/// <param name="LabelY">The baseline of the label.</param>
public sealed record StateDiagramEdge(string RuleKey, string Label, string? Detail, string? From, string To, string PathData, double LabelX, double LabelY);
