using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// The result of resolving a path as far as it exists, see <see cref="SubjectPathResolver.ResolvePartially"/>.
/// </summary>
public readonly struct SubjectPathResolution
{
    private readonly ImmutableArray<SubjectPathStep> _steps;

    /// <summary>
    /// Creates a resolution.
    /// </summary>
    public SubjectPathResolution(
        IInterceptorSubject? subject,
        IInterceptorSubject? deepestSubject,
        PropertyReference? nextProperty,
        ImmutableArray<SubjectPathStep> steps)
    {
        Subject = subject;
        DeepestSubject = deepestSubject;
        NextProperty = nextProperty;
        _steps = steps;
    }

    /// <summary>
    /// Gets the subject at the full path, or null when the path does not fully resolve.
    /// </summary>
    public IInterceptorSubject? Subject { get; }

    /// <summary>
    /// Gets the subject the resolved part of the path leads to: <see cref="Subject"/> when the path fully
    /// resolves, the subject the path starts from when not even its first segment resolves, and null when
    /// the subject the path starts from does not exist.
    /// </summary>
    public IInterceptorSubject? DeepestSubject { get; }

    /// <summary>
    /// Gets the property of <see cref="DeepestSubject"/> that holds, or would hold, the first segment that does
    /// not resolve, so a write to it can make the path resolve further. Null when the path fully resolves, when
    /// the subject the path starts from does not exist, or when no write to a property of
    /// <see cref="DeepestSubject"/> can resolve the segment: an unknown property name on a subject without an
    /// <c>[InlinePaths]</c> property, a route path that ends at a collection or dictionary without an index,
    /// or a canonical segment that names a collection or dictionary without a bracket index.
    /// </summary>
    public PropertyReference? NextProperty { get; }

    /// <summary>
    /// Gets the resolved segments in path order, starting after the subject the path starts from.
    /// The subject of the last step is <see cref="DeepestSubject"/>.
    /// </summary>
    public ImmutableArray<SubjectPathStep> Steps => _steps.IsDefault ? [] : _steps;
}

/// <summary>
/// A resolved path segment.
/// </summary>
/// <param name="Property">The property of the previous subject that holds <paramref name="Subject"/>.</param>
/// <param name="Index">The collection index or dictionary key within <paramref name="Property"/>, or null for a subject reference.</param>
/// <param name="Subject">The subject the segment leads to.</param>
public readonly record struct SubjectPathStep(RegisteredSubjectProperty Property, string? Index, IInterceptorSubject Subject);
