using HomeBlaze.Services;
using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// A viewer that may do everything except what the sets name. A member it may not read has no readable history either.
/// A method that requires administration runs only when the viewer may administer its member.
/// Linked into HomeBlaze.AI.Tests, HomeBlaze.Authorization.Blazor.Tests, HomeBlaze.History.Blazor.Tests,
/// HomeBlaze.Host.Services.Tests, HomeBlaze.Host.Tests, HomeBlaze.Storage.Blazor.Tests and the widget tests of the device libraries.
/// The sets may change during a test; <see cref="RaiseChanged"/> then tells the components.
/// </summary>
public sealed class TestUserPermissions : IUserPermissions
{
    /// <summary>The members the viewer may not read.</summary>
    public HashSet<RegisteredSubjectProperty> CannotRead { get; init; } = [];

    /// <summary>The members the viewer may not write.</summary>
    public HashSet<RegisteredSubjectProperty> CannotWrite { get; init; } = [];

    /// <summary>The members whose methods the viewer may not run.</summary>
    public HashSet<RegisteredSubjectProperty> CannotInvoke { get; init; } = [];

    /// <summary>The members the viewer may not administer.</summary>
    public HashSet<RegisteredSubjectProperty> CannotAdminister { get; init; } = [];

    /// <summary>The members whose recorded history the viewer may not read.</summary>
    public HashSet<RegisteredSubjectProperty> CannotReadHistory { get; init; } = [];

    /// <summary>The subjects the viewer may not see.</summary>
    public HashSet<IInterceptorSubject> CannotShow { get; init; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The roles the viewer holds, including the roles they include; <see cref="HoldsAllRoles"/> answers from it.</summary>
    public HashSet<string> HeldRoles { get; init; } = [];

    /// <summary>
    /// The roles of the viewer with deny rules; <see cref="GetRolesWithDenyRulesMissingFrom"/> names those the given roles
    /// do not name, as this viewer knows no included roles.
    /// </summary>
    public HashSet<string> RolesWithDenyRules { get; init; } = [];

    public event Action? Changed;

    public bool CanRead(RegisteredSubjectProperty member) => !CannotRead.Contains(member);

    public bool CanWrite(RegisteredSubjectProperty member) => !CannotWrite.Contains(member);

    public bool CanInvoke(RegisteredSubjectProperty member) =>
        !CannotInvoke.Contains(member) && (member.TryGetMethodMetadata()?.RequiresAdministration != true || CanAdminister(member));

    /// <summary>Called with the member right before <see cref="CanAdminister"/> answers, so that a test can change the sets between two checks.</summary>
    public Action<RegisteredSubjectProperty>? AdministerChecking { get; set; }

    public bool CanAdminister(RegisteredSubjectProperty member)
    {
        AdministerChecking?.Invoke(member);
        return !CannotAdminister.Contains(member);
    }

    public bool CanReadHistory(RegisteredSubjectProperty member) => CanRead(member) && !CannotReadHistory.Contains(member);

    public bool CanShow(RegisteredSubject subject) => !CannotShow.Contains(subject.Subject);

    /// <summary>True when <see cref="HeldRoles"/> holds every role, or everything; false for an empty set.</summary>
    public bool HoldsAllRoles(IReadOnlyCollection<string> roles) =>
        roles.Count > 0 && (IsHeld("everything") || roles.All(IsHeld));

    /// <summary>True when the viewer holds every role and the roles name each of <see cref="RolesWithDenyRules"/>, or everything for an empty set.</summary>
    public bool CanActThrough(IReadOnlyCollection<string> roles) =>
        roles.Count > 0 ? HoldsAllRoles(roles) && GetRolesWithDenyRulesMissingFrom(roles).Count == 0 : IsHeld("everything");

    public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) =>
        roles.Count == 0 ? [] : RolesWithDenyRules.Where(role => !roles.Contains(role, StringComparer.Ordinal)).ToList();

    private bool IsHeld(string role) => HeldRoles.Contains(role, StringComparer.Ordinal);

    /// <summary>The number of handlers subscribed to <see cref="Changed"/>.</summary>
    public int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Raises <see cref="Changed"/>, as the application does after a role change.</summary>
    public void RaiseChanged() => Changed?.Invoke();
}
