namespace HomeBlaze.Abstractions.Services;

/// <summary>
/// Type of subject method.
/// </summary>
public enum SubjectMethodKind
{
    /// <summary>
    /// Method with side effects.
    /// </summary>
    Operation,

    /// <summary>
    /// Method without side effects that returns a result.
    /// </summary>
    Query
}
