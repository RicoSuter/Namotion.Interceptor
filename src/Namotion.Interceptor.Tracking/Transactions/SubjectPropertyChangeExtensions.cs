using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Extension methods for <see cref="SubjectPropertyChange"/> used in transaction processing.
/// </summary>
internal static class SubjectPropertyChangeExtensions
{
    /// <summary>
    /// Creates rollback changes for a collection, reversing order for proper undo sequence.
    /// </summary>
    /// <param name="changes">The changes to create rollbacks for.</param>
    /// <returns>Rollback changes in reverse order.</returns>
    public static List<SubjectPropertyChange> ToRollbackChanges(
        this IEnumerable<SubjectPropertyChange> changes) =>
        changes.Reverse().Select(c => SubjectPropertyChange.Create(
            c.Property,
            source: c.Source,
            changedTimestamp: c.ChangedTimestamp,
            receivedTimestamp: c.ReceivedTimestamp,
            oldValue: c.GetNewValue<object?>(),
            newValue: c.GetOldValue<object?>())).ToList();

    /// <summary>
    /// Applies a single property change to the in-process model.
    /// </summary>
    /// <param name="change">The change to apply.</param>
    /// <param name="error">The exception if the change failed, null otherwise.</param>
    /// <returns>True if successful, false if an exception occurred.</returns>
    public static bool TryApplyChange(this SubjectPropertyChange change, out Exception? error)
    {
        try
        {
            var metadata = change.Property.Metadata;
            using (SubjectChangeContext.WithState(change.Source, change.ChangedTimestamp, change.ReceivedTimestamp))
            {
                metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
            }
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
    /// Applies all changes. On full success returns the input as the successful set (no copy); the
    /// failed/error lists are allocated only when a change fails.
    /// </summary>
    public static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChanges(this IReadOnlyList<SubjectPropertyChange> changes)
    {
        List<SubjectPropertyChange>? successful = null;
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.TryApplyChange(out var error))
            {
                successful?.Add(change);
            }
            else
            {
                if (failed is null)
                {
                    // First failure: materialize the successes seen so far.
                    successful = new List<SubjectPropertyChange>(i);
                    for (var j = 0; j < i; j++)
                    {
                        successful.Add(changes[j]);
                    }
                    failed = [];
                }

                failed.Add(change);
                if (error != null)
                {
                    (errors ??= []).Add(error);
                }
            }
        }

        return failed is null
            ? (changes, [], [])
            : (successful!, failed, errors ?? []);
    }
}
