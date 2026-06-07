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
    /// Applies all changes in the span except those whose <see cref="SubjectPropertyChange.Property"/>
    /// matches a change in <paramref name="exclude"/> (matched via a <see cref="HashSet{T}"/> of excluded
    /// properties). Inspect Failed.Count == 0 to detect full success. The Successful list is returned empty
    /// only on the no-exclude full-success path, where the caller already holds the input span and does not
    /// need the applied set; with exclusions, or on any failure, Successful is populated.
    /// </summary>
    public static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChanges(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange>? exclude)
    {
        HashSet<PropertyReference>? excluded = null;
        if (exclude is { Count: > 0 })
        {
            excluded = new HashSet<PropertyReference>(exclude.Count, PropertyReference.Comparer);
            foreach (var change in exclude)
            {
                excluded.Add(change.Property);
            }
        }

        return ApplyCore(changes, excluded);
    }

    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyCore(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference>? excluded)
    {
        // When excluded is null the applied set equals the input on success, so Successful stays null
        // (returned empty) until the first failure. When excluded is set the applied set differs from the
        // input, so Successful is materialized up front.
        List<SubjectPropertyChange>? successful = excluded is null ? null : new List<SubjectPropertyChange>(changes.Length);
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            if (excluded is not null && excluded.Contains(change.Property))
            {
                continue;
            }

            if (change.TryApplyChange(out var error))
            {
                successful?.Add(change);
            }
            else
            {
                if (failed is null)
                {
                    if (successful is null)
                    {
                        // First failure on the no-exclude path: materialize the successes seen so far.
                        successful = new List<SubjectPropertyChange>(i);
                        for (var j = 0; j < i; j++)
                        {
                            successful.Add(changes[j]);
                        }
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
            ? (successful ?? (IReadOnlyList<SubjectPropertyChange>)[], [], [])
            : (successful!, failed, errors ?? []);
    }
    
    private static bool TryApplyChange(this SubjectPropertyChange change, out Exception? error)
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
}
