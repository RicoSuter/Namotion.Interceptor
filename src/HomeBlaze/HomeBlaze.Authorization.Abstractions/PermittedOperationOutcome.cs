namespace HomeBlaze.Authorization.Abstractions;

/// <summary>How a method that a user started through <see cref="UserPermissionsExtensions"/> ended.</summary>
public enum PermittedOperationOutcome
{
    /// <summary>The method ran and returned.</summary>
    Completed,

    /// <summary>The user may not run the method, or there is no such method; it did not start.</summary>
    Denied,

    /// <summary>The method ran and threw.</summary>
    Failed
}
