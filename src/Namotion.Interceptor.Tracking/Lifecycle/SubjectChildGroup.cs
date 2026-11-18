namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The adjacent children of one subject in a span of <see cref="SubjectChild"/> entries, as
/// <see cref="SubjectChildExtensions.GroupBySubject"/> enumerates them.
/// </summary>
public readonly ref struct SubjectChildGroup
{
    internal SubjectChildGroup(IInterceptorSubject subject, ReadOnlySpan<SubjectChild> children)
    {
        Subject = subject;
        Children = children;
    }

    /// <summary>
    /// Gets the subject of the group.
    /// </summary>
    public IInterceptorSubject Subject { get; }

    /// <summary>
    /// Gets the children of the subject, at least one, in the order of the span.
    /// </summary>
    public ReadOnlySpan<SubjectChild> Children { get; }
}
