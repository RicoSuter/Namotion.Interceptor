namespace HomeBlaze.Components.Operations;

/// <summary>
/// What the user is asked before an operation or query runs.
/// </summary>
internal enum OperationPrompt
{
    /// <summary>The method runs without asking.</summary>
    None,

    /// <summary>The parameter dialog asks for the parameters that require input.</summary>
    Parameters,

    /// <summary>The confirmation dialog asks before the method runs.</summary>
    Confirmation,
}
