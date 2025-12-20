using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a failed source write operation.
/// </summary>
public sealed class SourceWriteFailure
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceWriteFailure"/> class.
    /// </summary>
    public SourceWriteFailure(object source, SubjectPropertyChange change, Exception error)
    {
        Source = source;
        Change = change;
        Error = error;
    }

    /// <summary>
    /// Gets the source that failed.
    /// </summary>
    public object Source { get; }

    /// <summary>
    /// Gets the change that failed to write.
    /// </summary>
    public SubjectPropertyChange Change { get; }

    /// <summary>
    /// Gets the error that occurred.
    /// </summary>
    public Exception Error { get; }
}
