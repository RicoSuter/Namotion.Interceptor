using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Represents the result of a write operation to a source.
/// </summary>
public sealed class SourceWriteResult
{
    /// <summary>
    /// Gets a shared instance representing a successful write with no failures.
    /// </summary>
    public static SourceWriteResult Success { get; } = new([]);

    /// <summary>
    /// Gets the changes that failed and should be retried (transient errors only).
    /// Permanent errors should not be included as they won't succeed on retry.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> FailedChanges { get; }

    public SourceWriteResult(IReadOnlyList<SubjectPropertyChange> failedChanges)
    {
        FailedChanges = failedChanges;
    }
}
