using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// Saves and deletes custom roles. A change is refused when it would break a principal whose roles work now, and is
/// committed before <see cref="Authorization.Roles"/> shows it; a refused change leaves everything unchanged. Renaming a
/// role renames it in the includes of the other custom roles and in every principal.
/// </summary>
public interface IRoleStore
{
    /// <summary>
    /// Saves <paramref name="role"/>: adds it when <paramref name="originalName"/> is null, else replaces the role of
    /// that name, renaming it when <see cref="AuthorizationRole.Name"/> differs.
    /// </summary>
    /// <param name="originalName">The name the role was loaded under, or null to add it.</param>
    /// <param name="role">The role as it is saved.</param>
    /// <param name="concurrencyStamp">The stamp the role was loaded with, see <see cref="GetConcurrencyStamp"/>; null when adding.</param>
    /// <param name="cancellationToken">Cancels the change until it is committed.</param>
    /// <returns>
    /// The new stamp when saved, with a warning when the role was renamed but a principal outside the database could
    /// not be renamed persistently; else why the change was refused.
    /// </returns>
    Task<RoleChangeResult> SaveRoleAsync(string? originalName, AuthorizationRole role, string? concurrencyStamp, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the role <paramref name="oldName"/> to <paramref name="newName"/>, keeping its includes and rules as the
    /// store holds them now, and renames it in the includes of the other custom roles and in every principal. Refused
    /// only when the role no longer exists, the new name is invalid or taken by another role, or a principal would break.
    /// </summary>
    /// <returns>As <see cref="SaveRoleAsync"/>.</returns>
    Task<RoleChangeResult> RenameRoleAsync(string oldName, string newName, CancellationToken cancellationToken);

    /// <summary>Deletes the role named <paramref name="name"/>; refused while a principal names it.</summary>
    Task<RoleChangeResult> DeleteRoleAsync(string name, string? concurrencyStamp, CancellationToken cancellationToken);

    /// <summary>Gets the stamp of the saved role named <paramref name="name"/>, or null when there is none.</summary>
    string? GetConcurrencyStamp(string name);
}

/// <summary>The outcome of a change of <see cref="IRoleStore"/>.</summary>
/// <param name="Succeeded">Whether the change was committed.</param>
/// <param name="Error">Why the change was refused; null when it succeeded.</param>
/// <param name="Warning">What a committed change could not complete, such as renaming the role in a saved configuration.</param>
/// <param name="ConcurrencyStamp">The new stamp of the saved role; null when refused or deleted.</param>
public readonly record struct RoleChangeResult(bool Succeeded, string? Error, string? Warning = null, string? ConcurrencyStamp = null);

/// <summary>The role names of one principal, with a label that names the principal in a refusal.</summary>
/// <param name="Principal">Names the principal, such as an agent's title or a user name.</param>
/// <param name="RoleNames">The names of the roles the principal holds.</param>
public readonly record struct PrincipalRoleSet(string Principal, IReadOnlyCollection<string> RoleNames);

/// <summary>A place outside the authentication database that names roles, such as an agent's configuration.</summary>
public interface IRoleNameReferences
{
    /// <summary>The role sets of every principal this place holds.</summary>
    IEnumerable<PrincipalRoleSet> GetRoleSets();

    /// <summary>Whether any principal names <paramref name="roleName"/>.</summary>
    bool IsUsed(string roleName);

    /// <summary>
    /// Renames <paramref name="oldName"/> to <paramref name="newName"/> in every principal: first in
    /// memory, so that the principals use the new name even when persisting fails, then persistently.
    /// </summary>
    /// <exception cref="Exception">Persisting failed; the message names the principals whose saved roles may still name <paramref name="oldName"/>.</exception>
    Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken);
}
