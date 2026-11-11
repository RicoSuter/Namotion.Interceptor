namespace Namotion.Interceptor.Authorization;

/// <summary>
/// Thrown by <see cref="AuthorizationDefinition.Create"/> and <see cref="AuthorizationDefinition.Compile"/> when the roles
/// cannot be compiled; the message names the role and the problem.
/// </summary>
public sealed class AuthorizationException : Exception
{
    public AuthorizationException()
    {
    }

    public AuthorizationException(string message)
        : base(message)
    {
    }

    public AuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
