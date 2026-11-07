namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// What the runs of one input did together: an error of an early rule survives a later successful rule of the same input.
/// </summary>
internal sealed class DispatchSummary
{
    private readonly HashSet<CompiledRule> _ran = [];

    public bool ChangedState { get; private set; }

    /// <summary>Whether a state change skipped a request of the same input.</summary>
    public bool SkippedForStateChange { get; set; }

    public bool ExecutedActions { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Whether the rule ran for this input already.</summary>
    public bool HasRun(CompiledRule rule) => _ran.Contains(rule);

    public void Add(CompiledRule rule, RuleRunResult result)
    {
        _ran.Add(rule);
        ChangedState |= result.ChangedState;
        ExecutedActions |= result.ExecutedActions;
        LastError = result.Error ?? LastError;
    }
}
