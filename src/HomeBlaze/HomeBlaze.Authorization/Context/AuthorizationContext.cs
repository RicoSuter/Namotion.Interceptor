using System.Security.Claims;

namespace HomeBlaze.Authorization.Context;

/// <summary>
/// Static context for authorization - uses AsyncLocal to flow user context
/// through the call stack including to interceptors.
/// </summary>
public static class AuthorizationContext
{
    private static readonly AsyncLocal<ClaimsPrincipal?> _currentUser = new();
    private static readonly AsyncLocal<HashSet<string>?> _expandedRoles = new();

    /// <summary>
    /// Gets the current user principal, or null if no user is set.
    /// </summary>
    public static ClaimsPrincipal? CurrentUser => _currentUser.Value;

    /// <summary>
    /// Gets the expanded roles for the current user (includes inherited roles).
    /// Returns empty set if no user is set.
    /// </summary>
    public static IReadOnlySet<string> ExpandedRoles => _expandedRoles.Value ?? EmptyRoles;

    private static readonly HashSet<string> EmptyRoles = [];

    /// <summary>
    /// Sets the current user and their expanded roles.
    /// Call this at the start of each request/circuit activity.
    /// </summary>
    /// <param name="user">The user principal.</param>
    /// <param name="expandedRoles">The user's roles after hierarchy expansion.</param>
    public static void SetUser(ClaimsPrincipal? user, IEnumerable<string> expandedRoles)
    {
        _currentUser.Value = user;
        _expandedRoles.Value = expandedRoles.ToHashSet();
    }

    /// <summary>
    /// Clears the current user context.
    /// </summary>
    public static void Clear()
    {
        _currentUser.Value = null;
        _expandedRoles.Value = null;
    }

    /// <summary>
    /// Checks if the current user has any of the required roles.
    /// </summary>
    /// <param name="requiredRoles">Roles that grant access (OR logic).</param>
    /// <returns>True if user has at least one of the required roles.</returns>
    public static bool HasAnyRole(IEnumerable<string> requiredRoles)
    {
        var userRoles = ExpandedRoles;
        return requiredRoles.Any(r => userRoles.Contains(r));
    }

    /// <summary>
    /// Checks if the current user is authenticated.
    /// </summary>
    public static bool IsAuthenticated => CurrentUser?.Identity?.IsAuthenticated == true;

    /// <summary>
    /// Extracts role claims from a ClaimsPrincipal.
    /// </summary>
    /// <param name="user">The user principal.</param>
    /// <returns>The user's role claim values.</returns>
    public static IEnumerable<string> GetRolesFromClaims(ClaimsPrincipal? user)
    {
        if (user == null) return [];
        return user.Claims
            .Where(c => c.Type == ClaimTypes.Role)
            .Select(c => c.Value);
    }

    /// <summary>
    /// Populates the authorization context from a user principal.
    /// Automatically adds Anonymous role for unauthenticated users.
    /// </summary>
    /// <param name="user">The user principal.</param>
    /// <param name="expandRoles">Function to expand roles using the role hierarchy.</param>
    public static void PopulateFromUser(ClaimsPrincipal? user, Func<IEnumerable<string>, IReadOnlySet<string>> expandRoles)
    {
        var userRoles = GetRolesFromClaims(user).ToList();

        // Add Anonymous role for unauthenticated users
        if (user?.Identity?.IsAuthenticated != true)
        {
            userRoles.Add(Roles.DefaultRoles.Anonymous);
        }

        var expandedRoles = expandRoles(userRoles);
        SetUser(user, expandedRoles);
    }
}
