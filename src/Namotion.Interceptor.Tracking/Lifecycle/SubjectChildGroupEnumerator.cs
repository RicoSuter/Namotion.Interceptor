namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Enumerates the groups of adjacent children of one subject in a span of <see cref="SubjectChild"/> entries, see
/// <see cref="SubjectChildExtensions.GroupBySubject"/>.
/// </summary>
public ref struct SubjectChildGroupEnumerator
{
    private ReadOnlySpan<SubjectChild> _remaining;

    internal SubjectChildGroupEnumerator(ReadOnlySpan<SubjectChild> children)
    {
        _remaining = children;
        Current = default;
    }

    /// <summary>
    /// Gets the group at the current position.
    /// </summary>
    public SubjectChildGroup Current { readonly get; private set; }

    /// <summary>
    /// Advances to the next group.
    /// </summary>
    /// <returns><c>true</c> if there is a next group; <c>false</c> at the end of the span.</returns>
    public bool MoveNext()
    {
        var remaining = _remaining;
        if (remaining.IsEmpty)
        {
            return false;
        }

        var subject = remaining[0].Subject;
        var end = 1;
        while (end < remaining.Length && ReferenceEquals(remaining[end].Subject, subject))
        {
            end++;
        }

        Current = new SubjectChildGroup(subject, remaining[..end]);
        _remaining = remaining[end..];
        return true;
    }

    /// <summary>
    /// Returns this enumerator, so that it can be used in a <c>foreach</c> loop.
    /// </summary>
    /// <returns>This enumerator.</returns>
    public readonly SubjectChildGroupEnumerator GetEnumerator() => this;
}
