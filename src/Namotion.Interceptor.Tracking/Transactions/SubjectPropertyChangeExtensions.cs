using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Extension methods for <see cref="SubjectPropertyChange"/> used in transaction processing.
/// </summary>
internal static class SubjectPropertyChangeExtensions
{
    /// <summary>
    /// Creates a rollback change by swapping old and new values.
    /// Used to revert a previously applied change.
    /// </summary>
    /// <param name="change">The original change to create a rollback for.</param>
    /// <returns>A new change that will restore the original value.</returns>
    public static SubjectPropertyChange ToRollbackChange(this SubjectPropertyChange change) =>
        SubjectPropertyChange.Create(
            change.Property,
            source: change.Source,
            changedTimestamp: change.ChangedTimestamp,
            receivedTimestamp: change.ReceivedTimestamp,
            oldValue: change.GetNewValue<object?>(),
            newValue: change.GetOldValue<object?>());

    /// <summary>
    /// Creates rollback changes for a collection, reversing order for proper undo sequence.
    /// </summary>
    /// <param name="changes">The changes to create rollbacks for.</param>
    /// <returns>Rollback changes in reverse order.</returns>
    public static IEnumerable<SubjectPropertyChange> ToRollbackChanges(
        this IEnumerable<SubjectPropertyChange> changes) =>
        changes.Reverse().Select(c => c.ToRollbackChange());

    /// <summary>
    /// Applies a single property change to the in-process model.
    /// </summary>
    /// <param name="change">The change to apply.</param>
    /// <param name="error">The exception if the change failed, null otherwise.</param>
    /// <returns>True if successful, false if an exception occurred.</returns>
    public static bool TryApply(this SubjectPropertyChange change, out Exception? error)
    {
        try
        {
            var metadata = change.Property.Metadata;
            metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Applies multiple property changes, collecting successes and failures.
    /// </summary>
    /// <param name="changes">The changes to apply.</param>
    /// <returns>Lists of successful changes, failed changes, and errors.</returns>
    public static (List<SubjectPropertyChange> Successful, List<SubjectPropertyChange> Failed, List<Exception> Errors)
        ApplyAll(this IEnumerable<SubjectPropertyChange> changes)
    {
        var successful = new List<SubjectPropertyChange>();
        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        foreach (var change in changes)
        {
            if (change.TryApply(out var error))
            {
                successful.Add(change);
            }
            else
            {
                failed.Add(change);
                if (error != null)
                {
                    errors.Add(error);
                }
            }
        }

        return (successful, failed, errors);
    }
}
