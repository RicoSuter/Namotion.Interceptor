using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Compiles the policies of principals from one set of roles. The rules of every role are compiled once, on the first
/// use, and shared by all principals; an error that every principal depends on becomes the error of each result.
/// </summary>
internal sealed class RoleCompiler
{
    private static readonly HomeBlazeMemberTagProvider TagProvider = new();

    private readonly Lazy<AuthorizationDefinition> _definition;

    public RoleCompiler(ImmutableArray<AuthorizationRole> roles, PathProviderBase pathProvider)
    {
        _definition = new Lazy<AuthorizationDefinition>(() =>
            AuthorizationDefinition.Create(roles, SystemRules.Rules, AuthorizationActions.All, pathProvider, TagProvider));
    }

    /// <summary>Compiles the policy of a principal holding <paramref name="roleNames"/>, or denies everything with the reason.</summary>
    public AuthorizationResult Compile(string[] roleNames)
    {
        try
        {
            return new AuthorizationResult(_definition.Value.Compile(roleNames), null);
        }
        catch (AuthorizationException exception)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, exception.Message);
        }
    }
}
