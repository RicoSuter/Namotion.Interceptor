using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction detects a concurrent modification conflict.
/// </summary>
/// <remarks>
/// Inherits from <see cref="TransactionException"/> for unified exception handling.
/// <see cref="TransactionException.AppliedChanges"/> and <see cref="TransactionException.FailedChanges"/>
/// are always empty since no writes are attempted when conflicts are detected at commit time.
/// </remarks>
public sealed class TransactionConflictException : TransactionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionConflictException"/> class.
    /// </summary>
    public TransactionConflictException(IReadOnlyList<PropertyReference> conflictingProperties)
        : base(
            $"Transaction conflict detected on {conflictingProperties.Count} property(ies): {string.Join(", ", conflictingProperties.Select(p => p.Name))}",
            Array.Empty<SubjectPropertyChange>(),
            Array.Empty<SourceWriteFailure>())
    {
        ConflictingProperties = conflictingProperties;
    }

    /// <summary>
    /// Gets the properties that were modified by another source during the transaction.
    /// </summary>
    public IReadOnlyList<PropertyReference> ConflictingProperties { get; }
}
