using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for discovering methods on subjects via MethodMetadata properties.
/// </summary>
public static class RegisteredSubjectMethodExtensions
{
    /// <summary>
    /// Gets the methods (operations and queries) of the subject that are offered to the target, so not excluded from
    /// it, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetAllMethods(this RegisteredSubject subject, ExclusionTarget target)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(property => property.GetValue())
            .OfType<MethodMetadata>()
            .Where(method => !method.IsExcludedFrom(target))
            .OrderBy(method => method.Position)
            .ToList();
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
    /// Gets whether the operation that the method property holds is in effect right now: the value of its
    /// <see cref="KnownAttributes.IsActive"/> attribute, false without one.
    /// </summary>
    public static bool IsMethodActive(this RegisteredSubjectProperty methodProperty)
    {
        return methodProperty.TryGetAttribute(KnownAttributes.IsActive)?.GetValue() as bool? ?? false;
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
