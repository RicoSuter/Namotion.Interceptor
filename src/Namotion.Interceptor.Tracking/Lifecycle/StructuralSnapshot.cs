using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal readonly record struct StructuralOccurrence(
    IInterceptorSubject Subject,
    int SubjectOrdinal,
    object? Index);

internal sealed record StructuralSnapshot(
    long SourceRevision,
    ImmutableArray<StructuralOccurrence> Occurrences)
{
    internal static readonly StructuralSnapshot Empty = new(0, []);
}
