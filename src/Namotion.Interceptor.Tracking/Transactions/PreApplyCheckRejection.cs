using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Maps a handler exception that rejected the pre-apply check to the errors the commit reports.
/// </summary>
internal static class PreApplyCheckRejection
{
    /// <summary>
    /// Returns the errors of a handler exception that rejected <paramref name="checkedChanges"/>. A
    /// <see cref="SubjectTransactionException"/> whose failed changes include a checked change contributes its
    /// errors; any other exception is the error itself.
    /// </summary>
    internal static IReadOnlyList<Exception> GetErrors(
        Exception exception,
        IReadOnlyList<SubjectPropertyChange> checkedChanges)
    {
        if (exception is SubjectTransactionException { FailedChanges.Count: > 0 } transactionException &&
            SubjectPropertyChangeOperations.IntersectByProperty(transactionException.FailedChanges, checkedChanges).Count > 0)
        {
            return GetCheckedChangeErrors(transactionException, checkedChanges);
        }

        return [exception];
    }

    /// <summary>
    /// Returns the errors of a rejection. When the failed changes and errors align one to one, only the errors of
    /// the failed changes that were checked are kept; otherwise all errors, or the exception itself when it has none.
    /// </summary>
    private static IReadOnlyList<Exception> GetCheckedChangeErrors(
        SubjectTransactionException exception,
        IReadOnlyList<SubjectPropertyChange> checkedChanges)
    {
        var failedChanges = exception.FailedChanges;
        var errors = exception.Errors;
        if (errors.Count == 0)
        {
            return [exception];
        }

        if (errors.Count != failedChanges.Count)
        {
            return errors;
        }

        var checkedProperties = new HashSet<PropertyReference>(checkedChanges.Count, PropertyReference.Comparer);
        foreach (var change in checkedChanges)
        {
            checkedProperties.Add(change.Property);
        }

        var alignedErrors = new List<Exception>(errors.Count);
        for (var index = 0; index < failedChanges.Count; index++)
        {
            if (checkedProperties.Contains(failedChanges[index].Property))
            {
                alignedErrors.Add(errors[index]);
            }
        }

        return alignedErrors;
    }
}
