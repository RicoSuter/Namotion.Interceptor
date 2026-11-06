namespace HomeBlaze.Automation.Runtime;

/// <summary>A validated configuration of an automation, in rule order. Replaced as a whole when the configuration changes.</summary>
internal sealed class CompiledAutomation(IReadOnlyList<CompiledRule> rules, IReadOnlyList<string> states, string? initialState)
{
    public static CompiledAutomation Empty { get; } = new([], [], null);

    public IReadOnlyList<CompiledRule> Rules { get; } = rules.ToArray();

    public IReadOnlyList<string> States { get; } = states;

    public string? InitialState { get; } = initialState;

    public bool HasState(string? state) => state is not null && States.Contains(state, StringComparer.Ordinal);

    public CompiledRule? FindRule(string key) => Rules.FirstOrDefault(rule => rule.Key == key);

    /// <summary>The same configuration with other rules, such as the rules that still exist after a failed reload.</summary>
    public CompiledAutomation WithRules(IReadOnlyList<CompiledRule> rules) => new(rules, States, InitialState);
}
