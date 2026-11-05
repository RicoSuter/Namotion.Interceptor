using Microsoft.AspNetCore.Authorization;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Requires that the principal may take an action on a member, the <see cref="RegisteredSubjectProperty"/> given as the
/// resource of <see cref="IAuthorizationService.AuthorizeAsync(System.Security.Claims.ClaimsPrincipal, object, IEnumerable{IAuthorizationRequirement})"/>,
/// decided as <see cref="PolicyUserPermissions"/> decides it: with the roles the <see cref="UserStore"/> holds now for the
/// signed-in person, or with those of the built-in user of visitors who are not signed in. Any other resource fails.
/// </summary>
public sealed class MemberActionRequirement : IAuthorizationRequirement
{
    private readonly Func<PermissionDecisions, RegisteredSubjectProperty, bool> _isAllowed;

    private MemberActionRequirement(string name, Func<PermissionDecisions, RegisteredSubjectProperty, bool> isAllowed)
    {
        Name = name;
        _isAllowed = isAllowed;
    }

    /// <summary>Reading the value, as <see cref="PolicyUserPermissions.CanRead"/>.</summary>
    public static MemberActionRequirement Read { get; } = new("read", static (decisions, member) => decisions.CanRead(member));

    /// <summary>Changing the value, as <see cref="PolicyUserPermissions.CanWrite"/>.</summary>
    public static MemberActionRequirement Write { get; } = new("write", static (decisions, member) => decisions.CanWrite(member));

    /// <summary>Running the method, as <see cref="PolicyUserPermissions.CanInvoke"/>: <c>administer</c> when it requires administration, else <c>execute</c>.</summary>
    public static MemberActionRequirement Execute { get; } = new("execute", static (decisions, member) => decisions.CanInvoke(member));

    /// <summary>Reading the recorded history, as <see cref="PolicyUserPermissions.CanReadHistory"/>.</summary>
    public static MemberActionRequirement History { get; } = new("history", static (decisions, member) => decisions.CanReadHistory(member));

    /// <summary>Administering the member, as <see cref="PolicyUserPermissions.CanAdminister"/>.</summary>
    public static MemberActionRequirement Administer { get; } = new("administer", static (decisions, member) => decisions.CanAdminister(member));

    /// <summary>The name of the action.</summary>
    public string Name { get; }

    internal bool IsAllowed(PermissionDecisions decisions, RegisteredSubjectProperty member) => _isAllowed(decisions, member);

    /// <inheritdoc />
    public override string ToString() => Name;
}
