using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authorization.Abstractions;

/// <summary>
/// What the person of the current circuit may see and do. Scoped per circuit; the default registration denies everything. The answers of one instance come from one snapshot of the person, so that they never belong to different people.
/// </summary>
public interface IUserPermissions
{
    /// <summary>Raised when the answers may have changed, such as after a role change; components render again.</summary>
    event Action? Changed;

    /// <summary>Whether the user may read the value of <paramref name="member"/>.</summary>
    bool CanRead(RegisteredSubjectProperty member);

    /// <summary>Whether the user may change the value of <paramref name="member"/>.</summary>
    bool CanWrite(RegisteredSubjectProperty member);

    /// <summary>Whether the user may run the method of <paramref name="member"/>: <c>administer</c> when it requires administration, else <c>execute</c>.</summary>
    bool CanInvoke(RegisteredSubjectProperty member);

    /// <summary>
    /// Whether the user may administer <paramref name="member"/>, such as change a role or a setting that only an
    /// administrator changes. Ask it for the member that is changed.
    /// </summary>
    bool CanAdminister(RegisteredSubjectProperty member);

    /// <summary>Whether the user may read the recorded history of <paramref name="member"/>.</summary>
    bool CanReadHistory(RegisteredSubjectProperty member);

    /// <summary>Whether the subject is shown: some member is readable and a readable chain of parents leads to a root.</summary>
    bool CanShow(RegisteredSubject subject);

    /// <summary>
    /// Whether the user holds every one of <paramref name="roles"/>, counting the roles they include, for the rules that
    /// depend on roles rather than on members. True for a full administrator; false for an empty set.
    /// </summary>
    /// <param name="roles">The role names, compared ordinally.</param>
    bool HoldsAllRoles(IReadOnlyCollection<string> roles);

    /// <summary>
    /// Whether the user may act through a principal holding <paramref name="roles"/>, such as chat with an agent: they
    /// hold every one of <paramref name="roles"/> (see <see cref="HoldsAllRoles"/>), and the roles, counting the roles
    /// they include, include each role of the user with deny rules, so that the principal cannot do what those rules
    /// deny the user. For an empty set, whether the user is a full administrator.
    /// </summary>
    /// <param name="roles">The role names, compared ordinally.</param>
    bool CanActThrough(IReadOnlyCollection<string> roles);

    /// <summary>
    /// Gets the roles of the user, counting the roles they include, that have deny rules, or whose rules are unknown, and
    /// that <paramref name="roles"/>, counting the roles they include, lack, ordered by name: the reason
    /// <see cref="CanActThrough"/> refuses a user who holds all of <paramref name="roles"/>. Empty when there is none, and
    /// for an empty set.
    /// </summary>
    /// <param name="roles">The role names, compared ordinally.</param>
    IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles);
}
