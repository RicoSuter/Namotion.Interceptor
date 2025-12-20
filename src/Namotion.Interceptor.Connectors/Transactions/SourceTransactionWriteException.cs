using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Transactions;

/// <summary>
/// Exception thrown when a source fails to write property changes during a transaction commit.
/// </summary>
/// <remarks>
/// <para>
/// This exception is collected during the commit process. The transaction continues attempting to
/// write to all sources even if some fail, maximizing the number of successful writes.
/// A <see cref="Namotion.Interceptor.Tracking.Transactions.TransactionException"/> is thrown at the
/// end containing all successful and failed changes.
/// </para>
/// <para>
/// Changes that failed to write are NOT applied to the in-process model to maintain consistency
/// with the external system. Successfully written changes are applied even if other sources fail.
/// </para>
/// </remarks>
public class SourceTransactionWriteException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceTransactionWriteException"/> class.
    /// </summary>
    /// <param name="source">The external data source that failed to write changes.</param>
    /// <param name="failedChanges">The collection of property changes that failed to write.</param>
    /// <param name="inner">The exception that caused the write failure.</param>
    public SourceTransactionWriteException(ISubjectSource source, IReadOnlyList<SubjectPropertyChange> failedChanges, Exception inner)
        : base($"Failed to write {failedChanges.Count} change(s) to source {source.GetType().Name}. See inner exception for details.", inner)
    {
        Source = source;
        FailedChanges = failedChanges;
    }

    /// <summary>
    /// Gets the external data source that failed to write changes.
    /// </summary>
    public new ISubjectSource Source { get; }

    /// <summary>
    /// Gets the collection of property changes that failed to write to the source.
    /// </summary>
    /// <remarks>
    /// This collection can be used to identify which specific properties and values failed
    /// to persist to the external system. The changes are in the same order they were
    /// grouped for the source during commit.
    /// </remarks>
    public IReadOnlyList<SubjectPropertyChange> FailedChanges { get; }
}
