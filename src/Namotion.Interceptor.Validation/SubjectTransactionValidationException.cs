using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Thrown by a commit when values in its final set are invalid, before anything is written. The transaction stays
/// retryable: fix the values and commit again. <see cref="SubjectTransactionException.FailedChanges"/> holds the
/// invalid changes and <see cref="SubjectTransactionException.Errors"/> their validation errors, in the same order.
/// </summary>
public sealed class SubjectTransactionValidationException : SubjectTransactionException
{
    /// <summary>
    /// Initializes a new instance for the invalid changes and their validation errors, in the same order.
    /// </summary>
    /// <param name="failedChanges">The invalid changes.</param>
    /// <param name="errors">The validation errors, one per invalid change.</param>
    public SubjectTransactionValidationException(
        IReadOnlyList<SubjectPropertyChange> failedChanges,
        IReadOnlyList<Exception> errors)
        : base(
            "One or more transaction values are invalid. No changes have been written.",
            [],
            failedChanges,
            errors,
            errors.Count == 1 ? errors[0] : new AggregateException("Multiple transaction values are invalid.", errors))
    {
    }
}
