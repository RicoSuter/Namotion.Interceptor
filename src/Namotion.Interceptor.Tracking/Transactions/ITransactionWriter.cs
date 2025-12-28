using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Handler for writing transaction changes to external sources.
/// Implement this interface and register it with <see cref="IInterceptorSubjectContext"/>
/// to enable external source writes during transaction commit.
/// </summary>
public interface ITransactionWriter
{
    /// <summary>
    /// Writes the specified changes to external sources.
    /// </summary>
    /// <param name="changes">The property changes to write.</param>
    /// <param name="failureHandling">The transaction mode controlling failure handling behavior.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result indicating which changes succeeded and which failed.</returns>
    Task<TransactionWriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionFailureHandling failureHandling,
        TransactionRequirement requirement,
        CancellationToken cancellationToken);
}
