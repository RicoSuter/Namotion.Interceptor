namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>
/// Keeps the last layout and returns it again while the states, the initial state and the rules draw the same, so that a
/// render computes no layout. The current state is not part of the layout.
/// </summary>
/// <remarks>
/// An instance serves one of the overloads of <c>Get</c>: a layout that the other overload built is returned for an
/// automation whose applied configuration did not change.
/// </remarks>
internal sealed class StateDiagramLayoutCache
{
    private string[] _states = [];
    private string? _initialState;
    private StateDiagramRule[] _rules = [];
    private StateDiagramLayout? _layout;
    private (string[] States, string? InitialState, Dictionary<string, AutomationRule> Rules, string[] RuleOrder) _configuration;

    /// <summary>
    /// The layout of the configuration the automation applied. Applying a configuration replaces these values, so while
    /// they are the same instances, nothing is compared or built.
    /// </summary>
    public StateDiagramLayout Get(Automation automation)
    {
        var configuration = (automation.States, automation.InitialState, automation.Rules, automation.RuleOrder);
        if (_layout is not null && _configuration == configuration)
        {
            return _layout;
        }

        _configuration = configuration;
        return Get(automation.States, automation.InitialState, [.. automation.GetRulesInOrder().Select(pair => StateDiagramRule.From(pair.Key, pair.Value))]);
    }

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
