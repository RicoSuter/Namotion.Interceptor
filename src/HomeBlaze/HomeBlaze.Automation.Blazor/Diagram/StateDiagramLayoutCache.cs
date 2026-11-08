namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>
/// Keeps the last layout and returns it again while the states, the initial state and the rules draw the same, so that a
/// render computes no layout. The current state is not part of the layout.
/// </summary>
internal sealed class StateDiagramLayoutCache
{
    private string[] _states = [];
    private string? _initialState;
    private StateDiagramRule[] _rules = [];
    private StateDiagramLayout? _layout;

    public StateDiagramLayout Get(IReadOnlyList<string> states, string? initialState, IReadOnlyList<StateDiagramRule> rules)
    {
        if (_layout is not null && _initialState == initialState && states.SequenceEqual(_states) && rules.SequenceEqual(_rules))
        {
            return _layout;
        }

        _states = [.. states];
        _initialState = initialState;
        _rules = [.. rules];
        _layout = StateDiagramLayout.Create(states, initialState, rules);
        return _layout;
    }
}
