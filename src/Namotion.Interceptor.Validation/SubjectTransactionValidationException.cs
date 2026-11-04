using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Thrown by a commit when values in its final set are invalid, before anything is written. The transaction stays
/// retryable: fix the values and commit again. <see cref="SubjectTransactionException.FailedChanges"/> holds the invalid
/// changes, then the invalid derived properties that depend on a changed property, each as a change whose old and new
/// value are its computed value. The valid changes are not listed, but they were not written either, so
/// <see cref="SubjectTransactionException.AppliedChanges"/> is empty. <see cref="SubjectTransactionException.Errors"/>
/// holds the validation errors in the same order: a <see cref="PropertyValidationException"/> per entry, holding each
/// result its validators returned.
/// </summary>
public sealed class SubjectTransactionValidationException : SubjectTransactionException
{
    /// <summary>
    /// Initializes a new instance for the invalid changes and their validation errors, in the same order.
    /// </summary>
    /// <param name="failedChanges">The invalid changes.</param>
    /// <param name="errors">The validation errors, one per invalid change.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="errors"/> is empty.</exception>
    public SubjectTransactionValidationException(
        IReadOnlyList<SubjectPropertyChange> failedChanges,
        IReadOnlyList<Exception> errors)
        : base(
            "One or more transaction values are invalid. No changes have been written.",
            [],
            failedChanges,
            errors.Count > 0 ? errors : throw new ArgumentException("At least one validation error is required.", nameof(errors)))
    {
    }
}
