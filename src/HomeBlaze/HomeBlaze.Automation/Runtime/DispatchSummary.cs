namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// What the runs of one input did together: an error of an early rule survives a later successful rule of the same input.
/// </summary>
internal sealed class DispatchSummary
{
    private readonly HashSet<CompiledRule> _ran = [];
    private HashSet<CompiledRule>? _deactivated;

    public bool ChangedState { get; private set; }

    /// <summary>Whether a state change skipped a request of the same input.</summary>
    public bool SkippedForStateChange { get; set; }

    public bool ExecutedActions { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Whether the rule ran for this input already.</summary>
    public bool HasRun(CompiledRule rule) => _ran.Contains(rule);

    /// <summary>Whether a state change of this input deactivated the rule, also when a later one activated it again.</summary>
    public bool WasDeactivated(CompiledRule rule) => _deactivated?.Contains(rule) == true;

    /// <summary>Records the rules of the requests from <paramref name="start"/> on that are no longer active, after a run changed the state.</summary>
    public void MarkDeactivated(IReadOnlyList<RunRequest> requests, int start, Func<CompiledRule, bool> isActive)
    {
        for (var index = start; index < requests.Count; index++)
        {
            var rule = requests[index].Rule;
            if (!isActive(rule))
            {
                (_deactivated ??= []).Add(rule);
            }
        }
    }

    public void Add(CompiledRule rule, RuleRunResult result)
    {
        _ran.Add(rule);
        ChangedState |= result.ChangedState;
        ExecutedActions |= result.ExecutedActions;
        LastError = result.Error ?? LastError;
    }
}
