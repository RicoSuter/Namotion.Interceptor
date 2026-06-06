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

    /// <summary>
    /// Applies all changes in the span except those whose <see cref="SubjectPropertyChange.Property"/>
    /// matches a change in <paramref name="exclude"/>. On full success with no exclusions the Successful
    /// list is returned empty; the caller already holds the input span, and no caller needs the applied
    /// set unless a change fails (or exclusions force a rebuild). Inspect Failed.Count == 0 to detect
    /// full success.
    /// </summary>
    /// <param name="changes">The changes to apply.</param>
    /// <param name="exclude">
    /// Changes to skip, matched by <see cref="SubjectPropertyChange.Property"/> equality, using a
    /// <see cref="HashSet{T}"/> of excluded properties.
    /// </param>
    public static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChanges(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange>? exclude)
    {
        if (exclude is null || exclude.Count == 0)
        {
            return ApplyAllChanges(changes);
        }

        var excludedProperties = new HashSet<PropertyReference>(exclude.Count, PropertyReference.Comparer);
        foreach (var change in exclude)
        {
            excludedProperties.Add(change.Property);
        }

        return ApplyAllChangesWithSetExclude(changes, excludedProperties);
    }

    /// <summary>
    /// On full success with no exclusions the Successful list is returned empty; the caller already
    /// holds the input span, and no caller needs the applied set unless a change fails (or exclusions
    /// force a rebuild). Inspect Failed.Count == 0 to detect full success.
    /// </summary>
    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        List<SubjectPropertyChange>? successful = null;
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        for (var i = 0; i < changes.Length; i++)
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
            ? ([], [], [])
            : (successful!, failed, errors ?? []);
    }

    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChangesWithSetExclude(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference> excludedProperties)
    {
        var successful = new List<SubjectPropertyChange>(changes.Length);
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            if (excludedProperties.Contains(change.Property))
            {
                continue;
            }

            ApplyOrCollect(change, successful, ref failed, ref errors);
        }

        return failed is null
            ? (successful, [], [])
            : (successful, failed, errors ?? []);
    }

    private static void ApplyOrCollect(
        SubjectPropertyChange change,
        List<SubjectPropertyChange> successful,
        ref List<SubjectPropertyChange>? failed,
        ref List<Exception>? errors)
    {
        if (change.TryApplyChange(out var error))
        {
            successful.Add(change);
        }
        else
        {
            (failed ??= []).Add(change);
            if (error != null)
            {
                (errors ??= []).Add(error);
            }
        }
    }

}
