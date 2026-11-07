using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Compiles the policies of principals from one set of roles. The rules of every role are compiled once per
/// <see cref="PrincipalKind"/>, on the first use of that kind, since each kind has its own system rules, and shared by
/// all principals of the kind; an error that every principal depends on becomes the error of each result.
/// </summary>
internal sealed class RoleCompiler
{
    private static readonly HomeBlazeMemberTagProvider TagProvider = new();

    private readonly Lazy<AuthorizationDefinition>[] _definitions;

    public RoleCompiler(ImmutableArray<AuthorizationRole> roles, PathProviderBase pathProvider)
    {
        var kinds = Enum.GetValues<PrincipalKind>();
        _definitions = new Lazy<AuthorizationDefinition>[kinds.Length];
        foreach (var kind in kinds)
        {
            _definitions[(int)kind] = new Lazy<AuthorizationDefinition>(() =>
                AuthorizationDefinition.Create(roles, SystemRules.For(kind), AuthorizationActions.All, pathProvider, TagProvider));
        }
    }

    /// <summary>Compiles the policy of a principal of <paramref name="kind"/> holding <paramref name="roleNames"/>, or denies everything with the reason.</summary>
    public AuthorizationResult Compile(string[] roleNames, PrincipalKind kind)
    {
        try
        {
            return new AuthorizationResult(_definitions[(int)kind].Value.Compile(roleNames), null);
        }
        catch (AuthorizationException exception)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, exception.Message);
        }
    }
}
