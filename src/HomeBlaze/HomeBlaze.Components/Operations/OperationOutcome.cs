namespace HomeBlaze.Components.Operations;

/// <summary>
/// How an operation or query that was started from the UI ended.
/// </summary>
public enum OperationOutcome
{
    /// <summary>The user cancelled the prompt, the caller went away before the method started, or the method was cancelled.</summary>
    Cancelled,

    /// <summary>The method ran and returned.</summary>
    Succeeded,

    /// <summary>The method threw.</summary>
    Failed,
}
