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
    /// Gets all methods (operations and queries) for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetAllMethods(this RegisteredSubject subject)
        => subject.GetAllMethods(MethodAudience.All);

    /// <summary>
    /// Gets the methods (operations and queries) of the subject that are meant for any of the given audiences, see
    /// <see cref="MethodMetadata.IsFor"/>, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetAllMethods(this RegisteredSubject subject, MethodAudience audience)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(p => p.GetValue())
            .OfType<MethodMetadata>()
            .Where(m => m.IsFor(audience))
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>
    /// Gets all operation methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetOperationMethods(this RegisteredSubject subject)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(p => p.GetValue())
            .OfType<MethodMetadata>()
            .Where(m => m.Kind == MethodKind.Operation)
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>
    /// Gets all query methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetQueryMethods(this RegisteredSubject subject)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(p => p.GetValue())
            .OfType<MethodMetadata>()
            .Where(m => m.Kind == MethodKind.Query)
            .OrderBy(m => m.Position)
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
