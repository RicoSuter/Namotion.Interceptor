using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction detects a concurrent modification conflict.
/// </summary>
/// <remarks>
/// <see cref="SubjectTransactionException.AppliedChanges"/> and <see cref="SubjectTransactionException.FailedChanges"/>
/// are always empty: conflicts are detected before anything is written.
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
    /// Gets the properties whose landed value changed since the transaction wrote them, including properties it wrote
    /// with their unchanged value (see <see cref="TransactionCheckMode.Deferred"/>), and, with either
    /// <see cref="TransactionConflictBehavior"/>, the written properties whose subject left the transaction's context
    /// (detached or moved to another context). Such a property conflicts
    /// again on retry until its subject is attached to the context again outside the transaction (a write inside it is
    /// only captured), or the transaction is disposed.
    /// </summary>
    public IReadOnlyList<PropertyReference> ConflictingProperties { get; }
}
