namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Type of subject method.
/// </summary>
public enum MethodKind
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
