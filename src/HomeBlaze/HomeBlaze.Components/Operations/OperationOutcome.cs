namespace HomeBlaze.Components.Operations;

/// <summary>
/// How an operation or query that was started from the UI ended.
/// </summary>
public enum OperationOutcome
{
    /// <summary>The user canceled the prompt, or the caller went away before the method started.</summary>
    Canceled,

    /// <summary>The method ran and returned.</summary>
    Succeeded,

    /// <summary>The method threw, also an <see cref="OperationCanceledException"/> such as the timeout of a request.</summary>
    Failed,

    /// <summary>The roles of the viewer do not allow running the method, checked before the prompt and before it starts.</summary>
    Denied,
}
