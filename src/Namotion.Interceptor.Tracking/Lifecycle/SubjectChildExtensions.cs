namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Extension methods for spans of <see cref="SubjectChild"/> entries.
/// </summary>
public static class SubjectChildExtensions
{
    /// <summary>
    /// Enumerates the children grouped by subject, in the order of each subject's first key or position, without
    /// allocating.
    /// </summary>
    /// <remarks>
    /// A group ends where the subject changes, so each subject is in exactly one group only while its entries are
    /// adjacent, as in the span <see cref="IChildIndexHandler.ReplaceChildIndices"/> receives. It is not a general
    /// grouping: a subject whose entries are not adjacent is in several groups.
    /// </remarks>
    /// <param name="children">The children, with the entries of each subject adjacent.</param>
    /// <returns>An enumerator over the groups.</returns>
    public static SubjectChildGroupEnumerator GroupBySubject(this ReadOnlySpan<SubjectChild> children)
        => new(children);
}
