using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for discovering HomeBlaze methods on subjects via registry method attributes.
/// </summary>
public static class RegisteredSubjectMethodExtensions
{
    /// <summary>
    /// Gets all methods (operations and queries) for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetAllMethods(this RegisteredSubject subject)
    {
        return subject.Methods
            .Select(m => m.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata)
            .Where(m => m is not null)
            .OrderBy(m => m!.Position)
            .ToList()!;
    }

    /// <summary>
    /// Gets all operation methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetOperationMethods(this RegisteredSubject subject)
    {
        return subject.Methods
            .Select(m => m.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata)
            .Where(m => m is not null && m.Kind == MethodKind.Operation)
            .OrderBy(m => m!.Position)
            .ToList()!;
    }

    /// <summary>
    /// Gets all query methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetQueryMethods(this RegisteredSubject subject)
    {
        return subject.Methods
            .Select(m => m.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata)
            .Where(m => m is not null && m.Kind == MethodKind.Query)
            .OrderBy(m => m!.Position)
            .ToList()!;
    }
}
