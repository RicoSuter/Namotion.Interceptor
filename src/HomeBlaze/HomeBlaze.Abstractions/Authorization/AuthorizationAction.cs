namespace HomeBlaze.Abstractions.Authorization;

/// <summary>
/// The type of access being requested for authorization.
/// </summary>
public enum AuthorizationAction
{
    /// <summary>
    /// Reading a property value.
    /// </summary>
    Read,

    /// <summary>
    /// Writing/setting a property value.
    /// </summary>
    Write,

    /// <summary>
    /// Invoking a method.
    /// </summary>
    Invoke
}
