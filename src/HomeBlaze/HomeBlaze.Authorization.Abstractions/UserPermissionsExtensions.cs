using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authorization.Abstractions;

/// <summary>
/// Answers and gates for a user by subject and member name. An operation or query starts only when
/// <see cref="IUserPermissions.CanInvoke"/> allows its member right before it starts; what the method itself writes is not checked.
/// An administration change is asked with <see cref="IUserPermissions.CanAdminister"/> for the concrete members the change
/// writes. Every answer about a subject that is not registered is false.
/// </summary>
public static class UserPermissionsExtensions
{
    /// <summary>
    /// Gets the member that holds a method of the subject, or null when the subject has no such method.
    /// </summary>
    /// <param name="subject">The subject that has the method.</param>
    /// <param name="methodName">The name of the C# method, such as <c>nameof(Device.TurnOnAsync)</c>.</param>
    public static RegisteredSubjectProperty? TryGetMethodMember(this IInterceptorSubject subject, string methodName)
    {
        var member = subject.TryGetRegisteredSubject()?.TryGetProperty(MethodMetadata.GetPropertyName(methodName));
        return member?.Type == typeof(MethodMetadata) ? member : null;
    }

    /// <summary>
    /// Gets whether the user may run a method of the subject; false when the subject has no such method.
    /// </summary>
    /// <param name="permissions">The permissions of the person who runs the method.</param>
    /// <param name="subject">The subject that has the method.</param>
    /// <param name="methodName">The name of the C# method, such as <c>nameof(Device.TurnOnAsync)</c>.</param>
    public static bool CanInvoke(this IUserPermissions permissions, IInterceptorSubject subject, string methodName) =>
        subject.TryGetMethodMember(methodName) is { } member && permissions.CanInvoke(member);

    /// <summary>
    /// Runs a method of the subject when the user may; see
    /// <see cref="TryInvokeAsync(IUserPermissions, RegisteredSubjectProperty, object?[], IServiceProvider?, CancellationToken)"/>.
    /// <see cref="PermittedOperationOutcome.Denied"/> when the subject has no such method.
    /// </summary>
    /// <param name="permissions">The permissions of the person who runs the method.</param>
    /// <param name="subject">The subject that has the method.</param>
    /// <param name="methodName">The name of the C# method, such as <c>nameof(Device.TurnOnAsync)</c>.</param>
    /// <param name="parameters">The values of the parameters that require input, in their order.</param>
    /// <param name="serviceProvider">Resolves the parameters of the method that come from services.</param>
    /// <param name="cancellationToken">Passed to the method.</param>
    public static Task<PermittedOperationResult> TryInvokeAsync(
        this IUserPermissions permissions,
        IInterceptorSubject subject,
        string methodName,
        object?[]? parameters,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken) =>
        subject.TryGetMethodMember(methodName) is { } member
            ? permissions.TryInvokeAsync(member, parameters, serviceProvider, cancellationToken)
            : Task.FromResult(new PermittedOperationResult(PermittedOperationOutcome.Denied, null, null));

    /// <summary>
    /// Runs the method of the member when <see cref="IUserPermissions.CanInvoke"/> allows it right before the method starts.
    /// Never throws for a failing method: its exception, an <see cref="OperationCanceledException"/> included, is returned
    /// as <see cref="PermittedOperationOutcome.Failed"/>.
    /// </summary>
    /// <param name="permissions">The permissions of the person who runs the method.</param>
    /// <param name="member">The member that holds the method; <see cref="PermittedOperationOutcome.Denied"/> when it holds none.</param>
    /// <param name="parameters">The values of the parameters that require input, in their order.</param>
    /// <param name="serviceProvider">Resolves the parameters of the method that come from services.</param>
    /// <param name="cancellationToken">Passed to the method.</param>
    public static async Task<PermittedOperationResult> TryInvokeAsync(
        this IUserPermissions permissions,
        RegisteredSubjectProperty member,
        object?[]? parameters,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        if (member.GetValue() is not MethodMetadata method || !permissions.CanInvoke(member))
        {
            return new PermittedOperationResult(PermittedOperationOutcome.Denied, null, null);
        }

        try
        {
            var result = await method.InvokeAsync(parameters, serviceProvider, cancellationToken);
            return new PermittedOperationResult(PermittedOperationOutcome.Completed, result, null);
        }
        catch (Exception exception)
        {
            return new PermittedOperationResult(PermittedOperationOutcome.Failed, null, exception);
        }
    }

    /// <summary>
    /// Gets whether <see cref="IUserPermissions.CanShow"/> allows the subject; false for a subject that is not registered.
    /// </summary>
    /// <param name="permissions">The permissions of the user.</param>
    /// <param name="subject">The subject.</param>
    public static bool CanShow(this IUserPermissions permissions, IInterceptorSubject subject) =>
        subject.TryGetRegisteredSubject() is { } registered && permissions.CanShow(registered);

    /// <summary>
    /// Gets whether the user is shown the value of <paramref name="member"/> where a path leads to it: the member is
    /// readable and its subject is shown, see <see cref="IUserPermissions.CanShow"/>, so that a branch the roles hide
    /// shows none of its values, also when a member below it is readable.
    /// </summary>
    /// <param name="permissions">The permissions of the user.</param>
    /// <param name="member">The member.</param>
    public static bool CanReadShown(this IUserPermissions permissions, RegisteredSubjectProperty member) =>
        permissions.CanRead(member) && permissions.CanShow(member.Parent);

    /// <summary>
    /// Gets whether the user may administer the member <paramref name="propertyName"/> of the subject; false when the
    /// subject has no such member.
    /// </summary>
    /// <param name="permissions">The permissions of the user.</param>
    /// <param name="subject">The subject that has the member.</param>
    /// <param name="propertyName">The name of the member, such as <c>nameof(Layout.Items)</c>.</param>
    public static bool CanAdminister(this IUserPermissions permissions, IInterceptorSubject subject, string propertyName) =>
        subject.TryGetRegisteredSubject()?.TryGetProperty(propertyName) is { } member && permissions.CanAdminister(member);

    /// <summary>
    /// Gets whether the user may administer every configuration member of the subject: what an edit component or a write
    /// of the whole configuration of the subject may change. True for a registered subject without configuration members.
    /// </summary>
    /// <remarks>
    /// A subject without configuration members is allowed only because each of its edit components checks the members it
    /// changes itself; an edit component of such a subject must gate its own writes.
    /// </remarks>
    /// <param name="permissions">The permissions of the user.</param>
    /// <param name="subject">The subject to configure.</param>
    public static bool CanAdministerConfiguration(this IUserPermissions permissions, IInterceptorSubject subject)
    {
        if (subject.TryGetRegisteredSubject() is not { } registered)
        {
            return false;
        }

        foreach (var member in registered.Properties)
        {
            if (member.TryGetAttribute(KnownAttributes.Configuration) is not null && !permissions.CanAdminister(member))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether the user may administer the member of each parent that holds the subject, which deleting the subject
    /// changes, such as the children of a folder or the nodes of a canvas; false for a subject without parents.
    /// </summary>
    /// <param name="permissions">The permissions of the user.</param>
    /// <param name="subject">The subject to delete.</param>
    public static bool CanAdministerHoldingMembers(this IUserPermissions permissions, IInterceptorSubject subject)
    {
        var parents = subject.TryGetRegisteredSubject()?.Parents ?? [];
        if (parents.Length == 0)
        {
            return false;
        }

        foreach (var parent in parents)
        {
            if (!permissions.CanAdminister(parent.Property))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether the user may delete the subject through its delete operation: run the operation that
    /// <paramref name="deleteOperationMember"/> holds, see <see cref="IUserPermissions.CanInvoke"/>, and administer the
    /// member of each parent that holds the subject, see <see cref="CanAdministerHoldingMembers"/>.
    /// </summary>
    /// <param name="permissions">The permissions of the user.</param>
    /// <param name="subject">The subject to delete.</param>
    /// <param name="deleteOperationMember">The member of the subject that holds its delete operation.</param>
    public static bool CanDelete(this IUserPermissions permissions, IInterceptorSubject subject, RegisteredSubjectProperty deleteOperationMember) =>
        permissions.CanInvoke(deleteOperationMember) && permissions.CanAdministerHoldingMembers(subject);
}
