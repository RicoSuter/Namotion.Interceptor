namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>A box of the state diagram at its top left corner.</summary>
/// <param name="State">The state, or null for the source of the rules that change the state from any state.</param>
/// <param name="IsInitial">Whether it is the initial state.</param>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
internal sealed record StateDiagramNode(string? State, bool IsInitial, double X, double Y);
