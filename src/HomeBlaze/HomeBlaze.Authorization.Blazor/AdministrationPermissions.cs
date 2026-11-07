using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Authorization.Blazor;

/// <summary>Whether the viewer may administer the members that the role and user editors change.</summary>
internal static class AdministrationPermissions
{
    /// <summary>
    /// Gets whether the viewer may add, rename, remove and change the roles of <paramref name="authorization"/>: administer
    /// its <see cref="Authorization.Roles"/>, which the role store replaces on every change. False when it is null or not
    /// registered.
    /// </summary>
    public static bool CanAdministerRoles(this IUserPermissions permissions, Authorization? authorization) =>
        permissions.CanAdminister(authorization, nameof(Authorization.Roles));

    /// <summary>
    /// Gets whether the viewer may administer the member named <paramref name="memberName"/> of <paramref name="subject"/>,
    /// such as an operation by its name without the <c>Async</c> suffix. False when the subject is null, not registered
    /// or has no such member.
    /// </summary>
    public static bool CanAdminister(this IUserPermissions permissions, IInterceptorSubject? subject, string memberName) =>
        subject?.TryGetRegisteredSubject()?.TryGetProperty(memberName) is { } member &&
        permissions.CanAdminister(member);
}
