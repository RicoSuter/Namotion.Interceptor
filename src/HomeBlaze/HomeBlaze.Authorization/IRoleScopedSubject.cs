using HomeBlaze.Authorization.Abstractions;

namespace HomeBlaze.Authorization;

/// <summary>
/// A subject that shares some of its members only with the people who may act through its roles, such as an agent with
/// its findings and its shared conversation: as <see cref="IUserPermissions.CanActThrough"/> decides, they hold every one of its
/// roles, and its roles hold each of their roles with deny rules, see <see cref="RoleDelegation"/>. The permissions of
/// people deny reading, writing, running and reading the history of those members to anyone else, full administrators
/// included; with no roles only a full administrator passes.
/// </summary>
public interface IRoleScopedSubject
{
    /// <summary>The roles a person must hold, every one of them, counting the roles they include; without blank or repeated names.</summary>
    IReadOnlyCollection<string> ScopeRoles { get; }

    /// <summary>Whether the member registered under <paramref name="memberName"/> is shared only with the holders of <see cref="ScopeRoles"/>.</summary>
    bool IsRoleScoped(string memberName);
}
