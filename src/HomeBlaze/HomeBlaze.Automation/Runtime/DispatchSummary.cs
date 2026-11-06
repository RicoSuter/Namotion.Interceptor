namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// What the runs of one input did together: an error of an early rule survives a later successful rule of the same input.
/// </summary>
internal sealed class DispatchSummary
{
    public bool ChangedState { get; private set; }

    public bool ExecutedActions { get; private set; }

    public string? LastError { get; private set; }

    public void Add(RuleRunResult result, string? saveError)
    {
        ChangedState |= result.ChangedState;
        ExecutedActions |= result.ExecutedActions;
        LastError = saveError ?? result.Error ?? LastError;
    }
}
