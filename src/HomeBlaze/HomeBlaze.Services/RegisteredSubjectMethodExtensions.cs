using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for discovering methods on subjects via MethodMetadata properties.
/// </summary>
public static class RegisteredSubjectMethodExtensions
{
    /// <summary>
    /// Gets the methods (operations and queries) of the subject that are offered to the target, so not excluded from
    /// it, ordered by Position. With <paramref name="excludeMethodsWithPurpose"/>, the methods that have a purpose other
    /// than <see cref="OperationPurpose.None"/> are left out.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetAllMethods(
        this RegisteredSubject subject, ExclusionTarget target, bool excludeMethodsWithPurpose = false)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(property => property.GetValue())
            .OfType<MethodMetadata>()
            .Where(method => !method.IsExcludedFrom(target) &&
                (!excludeMethodsWithPurpose || method.Purpose == OperationPurpose.None))
            .OrderBy(method => method.Position)
            .ToList();
    }

    /// <summary>
    /// Gets the delete operation of the subject that is offered to the target: the operation whose purpose is
    /// <see cref="OperationPurpose.Delete"/>, the one with the lowest position when there are several (an arbitrary one of
    /// those with equal positions), or null when it has none.
    /// </summary>
    public static MethodMetadata? GetDeleteOperation(this RegisteredSubject subject, ExclusionTarget target) =>
        GetOperation(subject, OperationPurpose.Delete, target);

    /// <summary>
    /// Gets the edit operation of the subject that is offered to the target: the operation whose purpose is
    /// <see cref="OperationPurpose.Edit"/>, the one with the lowest position when there are several (an arbitrary one of
    /// those with equal positions), or null when it has none.
    /// </summary>
    public static MethodMetadata? GetEditOperation(this RegisteredSubject subject, ExclusionTarget target) =>
        GetOperation(subject, OperationPurpose.Edit, target);

    /// <summary>
    /// Gets whether the viewer may save the edit component of the subject through its edit operation, see
    /// <see cref="GetEditOperation"/>: the subject offers one to user interfaces, it is enabled, see
    /// <see cref="IsMethodEnabled"/>, and <see cref="IUserPermissions.CanInvoke"/> allows its member. False for a subject
    /// without one and for a subject that is not registered.
    /// </summary>
    public static bool CanInvokeEditOperation(this IUserPermissions permissions, IInterceptorSubject subject) =>
        subject.TryGetRegisteredSubject() is { } registered &&
        registered.GetEditOperation(ExclusionTarget.UserInterface) is { } editOperation &&
        registered.TryGetProperty(editOperation.PropertyName) is { } member &&
        member.IsMethodEnabled() &&
        permissions.CanInvoke(member);

    /// <summary>
    /// Gets whether the viewer may save the edit component of the subject through its edit operation instead of
    /// administering its configuration: the subject has no configuration members, whose change needs
    /// <see cref="UserPermissionsExtensions.CanAdministerConfiguration"/> whatever saves it, and
    /// <see cref="CanInvokeEditOperation"/> allows its edit operation.
    /// </summary>
    public static bool CanSaveThroughEditOperation(this IUserPermissions permissions, IInterceptorSubject subject) =>
        !subject.HasConfigurationProperties() && permissions.CanInvokeEditOperation(subject);

    private static MethodMetadata? GetOperation(RegisteredSubject subject, OperationPurpose purpose, ExclusionTarget target)
    {
        MethodMetadata? operation = null;
        foreach (var property in subject.Properties)
        {
            if (property.TryGetMethodMetadata() is { Kind: MethodKind.Operation } method &&
                method.Purpose == purpose &&
                !method.IsExcludedFrom(target) &&
                (operation is null || method.Position < operation.Position))
            {
                operation = method;
            }
        }

        return operation;
    }

    /// <summary>
    /// Gets whether the operation or query that the method property holds is enabled: the value of its
    /// <see cref="KnownAttributes.IsEnabled"/> attribute, true without one.
    /// </summary>
    public static bool IsMethodEnabled(this RegisteredSubjectProperty methodProperty)
    {
        return methodProperty.TryGetAttribute(KnownAttributes.IsEnabled)?.GetValue() as bool? ?? true;
    }

    /// <summary>
    /// Gets why the operation or query that the method property holds is not enabled: the value of its
    /// <see cref="KnownAttributes.DisabledReason"/> attribute, null without one.
    /// </summary>
    public static string? GetMethodDisabledReason(this RegisteredSubjectProperty methodProperty)
    {
        return methodProperty.TryGetAttribute(KnownAttributes.DisabledReason)?.GetValue() as string;
    }

    /// <summary>
    /// Gets the operation or query that the property holds, or null when it holds none. The value of a property whose
    /// type cannot hold a <see cref="MethodMetadata"/> is not read.
    /// </summary>
    public static MethodMetadata? TryGetMethodMetadata(this RegisteredSubjectProperty property)
    {
        return CanContainMethodMetadata(property) ? property.GetValue() as MethodMetadata : null;
    }

    private static bool CanContainMethodMetadata(RegisteredSubjectProperty property)
    {
        // A metadata subclass may implement an interface not implemented by MethodMetadata itself.
        return property.Type.IsInterface ||
            property.Type.IsAssignableFrom(typeof(MethodMetadata)) ||
            typeof(MethodMetadata).IsAssignableFrom(property.Type);
    }
}
