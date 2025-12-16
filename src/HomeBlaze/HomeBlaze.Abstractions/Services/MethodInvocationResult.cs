namespace HomeBlaze.Abstractions.Services;

/// <summary>
/// Immutable result of a method invocation.
/// </summary>
public sealed class MethodInvocationResult
{
    /// <summary>
    /// Whether the invocation succeeded.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// The return value (null for void methods or on failure).
    /// </summary>
    public object? Result { get; }

    /// <summary>
    /// The exception if the invocation failed.
    /// </summary>
    public Exception? Exception { get; }

    private MethodInvocationResult(bool success, object? result, Exception? exception)
    {
        Success = success;
        Result = result;
        Exception = exception;
    }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static MethodInvocationResult Succeeded(object? result = null)
        => new(true, result, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static MethodInvocationResult Failed(Exception exception)
        => new(false, null, exception ?? throw new ArgumentNullException(nameof(exception)));
}
