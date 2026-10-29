using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction detects a concurrent modification conflict.
/// </summary>
/// <remarks>
/// Inherits from <see cref="SubjectTransactionException"/> for unified exception handling.
/// <see cref="SubjectTransactionException.AppliedChanges"/> and <see cref="SubjectTransactionException.FailedChanges"/>
/// are always empty since no writes are attempted when conflicts are detected at commit time.
/// </remarks>
public sealed class SubjectTransactionConflictException : SubjectTransactionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectTransactionConflictException"/> class.
    /// </summary>
    public SubjectTransactionConflictException(IReadOnlyList<PropertyReference> conflictingProperties)
        : base(
            $"Transaction conflict detected on {conflictingProperties.Count} property(ies): {string.Join(", ", conflictingProperties.Select(p => p.Name))}",
            [], [], [])
    {
        ConflictingProperties = conflictingProperties;
    }

    /// <summary>
    /// Gets the properties that were modified by another source during the transaction, which can include properties
    /// the transaction wrote with their unchanged value (see <see cref="TransactionCheckMode.Deferred"/>), and the
    /// written properties whose subject no longer belongs to the transaction's context (detached or moved to a context
    /// that does not resolve the transaction's interceptor), with either <see cref="TransactionConflictBehavior"/>. Such a
    /// property conflicts again on retry until its subject is attached to the context again outside the transaction (a
    /// write inside it is only captured) or the transaction is disposed.
    /// </summary>
    public IReadOnlyList<PropertyReference> ConflictingProperties { get; }
}
