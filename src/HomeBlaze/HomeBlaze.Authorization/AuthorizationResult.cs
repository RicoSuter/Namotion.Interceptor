using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// The policy of a principal, and why it is <see cref="AuthorizationPolicy.DenyAll"/> when its roles could not be used.
/// </summary>
/// <param name="Policy">The policy; <see cref="AuthorizationPolicy.DenyAll"/> when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the roles could not be used, or null.</param>
public readonly record struct AuthorizationResult(AuthorizationPolicy Policy, string? Error);
