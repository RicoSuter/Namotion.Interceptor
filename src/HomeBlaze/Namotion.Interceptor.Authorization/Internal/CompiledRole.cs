namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>A role with its rules compiled, or the error that compiling them raised.</summary>
/// <remarks>The error is kept as a message and thrown as a new exception each time, so concurrent compilations never share one instance.</remarks>
internal sealed record CompiledRole(AuthorizationRole Role, CompiledRule[] Allow, CompiledRule[] Deny, string? Error);
