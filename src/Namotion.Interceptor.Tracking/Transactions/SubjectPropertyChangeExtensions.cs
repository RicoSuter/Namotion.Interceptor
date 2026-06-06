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
    /// matches a change in <paramref name="exclude"/>. On full success (no exclusions and no apply
    /// failures) returns a list of the applied changes; the failed/error lists are allocated only when
    /// a change fails.
    /// </summary>
    /// <param name="changes">The changes to apply.</param>
    /// <param name="exclude">
    /// Changes to skip, matched by <see cref="SubjectPropertyChange.Property"/> equality. When this is
    /// an in-order subsequence of <paramref name="changes"/> (the common case) a two-pointer walk is
    /// used; otherwise it falls back to a <see cref="HashSet{T}"/> of excluded properties.
    /// </param>
    public static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChanges(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange>? exclude)
    {
        if (exclude is null || exclude.Count == 0)
        {
            return ApplyAllChanges(changes);
        }

        // Common case: exclude is an in-order subsequence of changes (the writer reports source-write
        // failures in the same order). Walk both with two pointers; fall back to a HashSet otherwise.
        if (IsInOrderSubsequence(changes, exclude))
        {
            return ApplyAllChangesWithSubsequenceExclude(changes, exclude);
        }

        var excludedProperties = new HashSet<PropertyReference>(exclude.Count, PropertyReference.Comparer);
        foreach (var change in exclude)
        {
            excludedProperties.Add(change.Property);
        }

        return ApplyAllChangesWithSetExclude(changes, excludedProperties);
    }

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
            ? (CopyToList(changes), [], [])
            : (successful!, failed, errors ?? []);
    }

    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyAllChangesWithSubsequenceExclude(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange> exclude)
    {
        var successful = new List<SubjectPropertyChange>(changes.Length - exclude.Count);
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        var excludeIndex = 0;
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            if (excludeIndex < exclude.Count && PropertyReference.Comparer.Equals(change.Property, exclude[excludeIndex].Property))
            {
                excludeIndex++;
                continue;
            }

            ApplyOrCollect(change, successful, ref failed, ref errors);
        }

        return failed is null
            ? (successful, [], [])
            : (successful, failed, errors ?? []);
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

    private static bool IsInOrderSubsequence(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange> exclude)
    {
        if (exclude.Count > changes.Length)
        {
            return false;
        }

        var excludeIndex = 0;
        for (var i = 0; i < changes.Length && excludeIndex < exclude.Count; i++)
        {
            if (PropertyReference.Comparer.Equals(changes[i].Property, exclude[excludeIndex].Property))
            {
                excludeIndex++;
            }
        }

        return excludeIndex == exclude.Count;
    }

    private static List<SubjectPropertyChange> CopyToList(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        var list = new List<SubjectPropertyChange>(changes.Length);
        foreach (var change in changes)
        {
            list.Add(change);
        }

        return list;
    }
}
