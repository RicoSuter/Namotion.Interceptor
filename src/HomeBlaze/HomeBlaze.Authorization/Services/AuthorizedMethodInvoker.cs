using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Abstractions.Services;
using HomeBlaze.Authorization.Context;
using Namotion.Interceptor;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Decorator for ISubjectMethodInvoker that enforces authorization before method invocation.
/// </summary>
public class AuthorizedMethodInvoker : ISubjectMethodInvoker
{
    private readonly ISubjectMethodInvoker _inner;
    private readonly IAuthorizationResolver _authorizationResolver;

    public AuthorizedMethodInvoker(
        ISubjectMethodInvoker inner,
        IAuthorizationResolver authorizationResolver)
    {
        _inner = inner;
        _authorizationResolver = authorizationResolver;
    }

    public async Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] parameters,
        CancellationToken cancellationToken = default)
    {
        // Determine entity type from method kind
        var entity = method.Kind == SubjectMethodKind.Query
            ? AuthorizationEntity.Query
            : AuthorizationEntity.Operation;

        // Resolve required roles for this method
        var requiredRoles = _authorizationResolver.ResolveMethodRoles(
            subject,
            entity, method.MethodInfo.Name);

        // Check authorization
        if (!AuthorizationContext.HasAnyRole(requiredRoles))
        {
            var userName = AuthorizationContext.CurrentUser?.Identity?.Name ?? "anonymous";
            return MethodInvocationResult.Failed(new UnauthorizedAccessException(
                $"Access denied: User '{userName}' cannot invoke {method.Title}. " +
                $"Required roles: [{string.Join(", ", requiredRoles)}]"));
        }

        // Invoke the actual method
        return await _inner.InvokeAsync(subject, method, parameters, cancellationToken);
    }
}
