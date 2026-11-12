using System.Security.Claims;

namespace HomeBlaze.Authorization.Abstractions;

/// <summary>
/// Creates the <see cref="IUserPermissions"/> of a request outside a circuit, such as a request for a file, from the user
/// the request authenticated. The answers are decided when they are created and belong to that request only; they are
/// not revalidated and never raise <see cref="IUserPermissions.Changed"/>, as the request itself is validated where it
/// arrives. A user whose decisions cannot be computed is denied everything.
/// </summary>
public interface IRequestUserPermissionsFactory
{
    /// <summary>Creates the permissions of <paramref name="user"/>, the user of the request.</summary>
    /// <param name="user">The user the request authenticated; an unauthenticated principal is a visitor who is not signed in.</param>
    IUserPermissions Create(ClaimsPrincipal user);
}
