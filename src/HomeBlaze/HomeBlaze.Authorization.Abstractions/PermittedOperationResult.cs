namespace HomeBlaze.Authorization.Abstractions;

/// <summary>The end of a method that a user started through <see cref="UserPermissionsExtensions"/>.</summary>
/// <param name="Outcome">How the method ended.</param>
/// <param name="Result">What the method returned, or null when it returned nothing or did not complete.</param>
/// <param name="Exception">What the method threw when it failed, else null.</param>
public readonly record struct PermittedOperationResult(PermittedOperationOutcome Outcome, object? Result, Exception? Exception);
