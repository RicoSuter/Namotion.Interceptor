using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Claims transformation that expands role claims according to the role hierarchy.
/// This enables standard ASP.NET Core authorization (e.g., [Authorize(Roles = "User")])
/// to work with role inheritance (e.g., Admin automatically has User role).
/// </summary>
public class RoleHierarchyClaimsTransformation : IClaimsTransformation
{
    private readonly IRoleExpander _expander;

    public RoleHierarchyClaimsTransformation(IRoleExpander expander)
    {
        _expander = expander;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        // Get current role claims
        var existingRoles = principal
            .FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();

        if (existingRoles.Count == 0)
        {
            return Task.FromResult(principal);
        }

        // Expand roles according to hierarchy
        var expandedRoles = _expander.ExpandRoles(existingRoles);

        // Add any new roles that aren't already present
        foreach (var role in expandedRoles)
        {
            if (!principal.IsInRole(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        return Task.FromResult(principal);
    }
}
