using System.Security.Claims;
using HomeBlaze.Authorization;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Who a principal is to the policy: a signed-in person, a visitor who is not signed in, or not known yet.</summary>
internal sealed class UserIdentity
{
    private UserIdentity(string? userId, bool isSignedIn, bool isKnown)
    {
        UserId = userId;
        IsSignedIn = isSignedIn;
        IsKnown = isKnown;
    }

    /// <summary>The identity while the authentication state is not known; it holds no roles.</summary>
    public static UserIdentity Unknown { get; } = new(null, isSignedIn: false, isKnown: false);

    /// <summary>A visitor who is not signed in; holds the roles of <see cref="NameRules.AnonymousUserName"/>.</summary>
    public static UserIdentity SignedOut { get; } = new(null, isSignedIn: false, isKnown: true);

    /// <summary>The id of the signed-in person; null when not signed in, or when the principal names no user.</summary>
    public string? UserId { get; }

    public bool IsSignedIn { get; }

    private bool IsKnown { get; }

    public PrincipalKind Kind => IsSignedIn ? PrincipalKind.Person : PrincipalKind.Anonymous;

    public static UserIdentity From(ClaimsPrincipal principal, string userIdClaimType) =>
        principal.Identity?.IsAuthenticated == true
            ? new UserIdentity(principal.FindFirstValue(userIdClaimType), isSignedIn: true, isKnown: true)
            : SignedOut;

    /// <summary>
    /// Whether the signed-in person must change their password before they may see or do anything, as
    /// <see cref="UserStore.RequiresPasswordChange"/> says now; also true for a signed-in principal that names no user.
    /// False for a visitor and while unknown, who hold the roles of <see cref="GetRoles"/>.
    /// </summary>
    public bool RequiresPasswordChange(UserStore userStore) =>
        IsSignedIn && (UserId is null || userStore.RequiresPasswordChange(UserId));

    /// <summary>Gets the roles, the kind and whether the person must change their password, which the policy is decided from.</summary>
    public (IReadOnlyCollection<string> Roles, PrincipalKind Kind, bool MustChangePassword) GetPolicyInputs(UserStore userStore) =>
        (GetRoles(userStore), Kind, RequiresPasswordChange(userStore));

    /// <summary>
    /// Gets the roles as stored now: those the person acts with (see <see cref="UserStore.GetRoles"/>), those of
    /// <see cref="NameRules.AnonymousUserName"/> for a visitor, and none while unknown.
    /// </summary>
    public IReadOnlyCollection<string> GetRoles(UserStore userStore)
    {
        if (IsSignedIn)
        {
            return UserId is null ? [] : userStore.GetRoles(UserId);
        }

        return IsKnown ? userStore.AnonymousRoles : [];
    }
}
