namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// What the runs of one input did together: an error of an early rule survives a later successful rule of the same input.
/// </summary>
internal sealed class DispatchSummary
{
    public bool ChangedState { get; private set; }

    /// <summary>Whether a state change skipped a request of the same input.</summary>
    public bool SkippedForStateChange { get; set; }

    public bool ExecutedActions { get; private set; }

    public string? LastError { get; private set; }

    public void Add(RuleRunResult result, string? saveError)
    {
        ChangedState |= result.ChangedState;
        ExecutedActions |= result.ExecutedActions;
        LastError = saveError ?? result.Error ?? LastError;
    }
}
