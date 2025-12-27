namespace HomeBlaze.Abstractions.Authorization;

/// <summary>
/// Categorizes properties and methods for authorization purposes.
/// </summary>
public enum AuthorizationEntity
{
    /// <summary>
    /// Runtime state properties (sensor readings, device status).
    /// Default read: Guest, Default write: Operator.
    /// </summary>
    State,

    /// <summary>
    /// Persisted configuration properties (settings, parameters).
    /// Default read: User, Default write: Supervisor.
    /// </summary>
    Configuration,

    /// <summary>
    /// Read-only methods that don't change state (idempotent queries).
    /// Default invoke: User.
    /// </summary>
    Query,

    /// <summary>
    /// Methods that change state or have side effects.
    /// Default invoke: Operator.
    /// </summary>
    Operation
}
