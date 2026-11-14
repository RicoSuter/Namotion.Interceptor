namespace HomeBlaze.Components.Operations;

/// <summary>
/// The result of an operation or query that was started from the UI.
/// </summary>
/// <param name="Outcome">How the invocation ended.</param>
/// <param name="Parameters">
/// The values entered in the parameter dialog keyed by parameter name, or null when no parameter dialog was shown or it
/// was canceled. Set for every outcome after the dialog closed with Execute, also when the method then failed or did not
/// start.
/// </param>
/// <param name="Value">The value the method returned, or null when it returned nothing or did not succeed.</param>
public sealed record OperationInvocationResult(
    OperationOutcome Outcome,
    IReadOnlyDictionary<string, object?>? Parameters,
    object? Value);
