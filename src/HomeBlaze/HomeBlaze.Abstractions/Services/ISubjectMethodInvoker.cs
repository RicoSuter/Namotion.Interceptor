using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Services;

/// <summary>
/// Service for invoking subject methods (operations and queries).
/// </summary>
public interface ISubjectMethodInvoker
{
    /// <summary>
    /// Invokes a method on a subject with the specified parameters.
    /// </summary>
    /// <param name="subject">The subject to invoke the method on.</param>
    /// <param name="method">The method to invoke.</param>
    /// <param name="parameters">The parameter values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The invocation result.</returns>
    Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] parameters,
        CancellationToken cancellationToken = default);
}
