using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Operations and extension methods for <see cref="SubjectPropertyChange"/> used in transaction processing.
/// </summary>
internal static class SubjectPropertyChangeOperations
{
    /// <summary>
    /// Creates rollback changes for a collection, reversing order for proper undo sequence.
    /// </summary>
    /// <param name="changes">The changes to create rollbacks for.</param>
    /// <returns>Rollback changes in reverse order.</returns>
    public static IEnumerable<SubjectPropertyChange> ToRollbackChanges(
        this IEnumerable<SubjectPropertyChange> changes) =>
        changes.Reverse().Select(c => SubjectPropertyChange.Create(
            c.Property,
            source: c.Source,
            changedTimestamp: c.ChangedTimestamp,
            receivedTimestamp: c.ReceivedTimestamp,
            oldValue: c.GetNewValue<object?>(),
            newValue: c.GetOldValue<object?>()));

    /// <summary>
    /// Applies all changes in the span except those whose <see cref="SubjectPropertyChange.Property"/>
    /// matches a change in <paramref name="exclude"/> (matched via a <see cref="HashSet{T}"/> of excluded
    /// properties). Inspect Failed.Count == 0 to detect full success. The Successful list is returned empty
    /// only on the no-exclude full-success path, where the caller already holds the input span and does not
    /// need the applied set; with exclusions, or on any failure, Successful is populated.
    /// </summary>
    public static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange>? exclude)
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

        return ApplyLocalChanges(changes, excluded);
    }

    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference>? excluded)
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

            if (change.TryApplyLocalChange(out var error))
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
    
    private static bool TryApplyLocalChange(this SubjectPropertyChange change, out Exception? error)
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
    /// Reverts previously-applied local changes by applying their inverse values in reverse order.
    /// Returns any revert failures and errors so the caller can fold them into the exception.
    /// </summary>
    internal static (IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors) RevertLocalChanges(
        IReadOnlyList<SubjectPropertyChange> applied)
    {
        var rollback = applied.ToRollbackChanges().ToList();
        var (_, revertFailed, revertErrors) = ApplyLocalChanges(
            CollectionsMarshal.AsSpan(rollback), exclude: null);
        return (revertFailed, revertErrors);
    }

    /// <summary>
    /// Returns the subset of <paramref name="written"/> whose property also appears in
    /// <paramref name="failed"/> (matched by <see cref="SubjectPropertyChange.Property"/>).
    /// </summary>
    internal static IReadOnlyList<SubjectPropertyChange> IntersectByProperty(
        IReadOnlyList<SubjectPropertyChange> failed,
        IReadOnlyList<SubjectPropertyChange> written)
    {
        if (failed.Count == 0 || written.Count == 0)
        {
            return [];
        }

        var failedProperties = new HashSet<PropertyReference>(failed.Count, PropertyReference.Comparer);
        foreach (var change in failed)
        {
            failedProperties.Add(change.Property);
        }

        List<SubjectPropertyChange>? result = null;
        foreach (var change in written)
        {
            if (failedProperties.Contains(change.Property))
            {
                (result ??= new List<SubjectPropertyChange>(failed.Count)).Add(change);
            }
        }

        return result ?? (IReadOnlyList<SubjectPropertyChange>)[];
    }

    /// <summary>
    /// Returns the changes in <paramref name="changes"/> whose property is in neither
    /// <paramref name="excludeFirst"/> nor <paramref name="excludeSecond"/> (matched by
    /// <see cref="SubjectPropertyChange.Property"/>). Used to collect the local (no-source) changes that
    /// were neither written to a source nor failed at a source.
    /// </summary>
    internal static IReadOnlyList<SubjectPropertyChange> ExcludeByProperty(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> excludeFirst,
        IReadOnlyList<SubjectPropertyChange> excludeSecond)
    {
        if (excludeFirst.Count == 0 && excludeSecond.Count == 0)
        {
            return changes.ToArray();
        }

        var excluded = new HashSet<PropertyReference>(
            excludeFirst.Count + excludeSecond.Count, PropertyReference.Comparer);
        foreach (var change in excludeFirst)
        {
            excluded.Add(change.Property);
        }
        foreach (var change in excludeSecond)
        {
            excluded.Add(change.Property);
        }

        List<SubjectPropertyChange>? result = null;
        foreach (var change in changes)
        {
            if (!excluded.Contains(change.Property))
            {
                (result ??= []).Add(change);
            }
        }

        return result ?? (IReadOnlyList<SubjectPropertyChange>)[];
    }

    internal static IReadOnlyList<T> Concat<T>(params ReadOnlySpan<IReadOnlyList<T>> lists)
    {
        var total = 0;
        IReadOnlyList<T>? single = null;
        var nonEmptyCount = 0;
        foreach (var list in lists)
        {
            if (list.Count == 0) continue;
            total += list.Count;
            single = list;
            nonEmptyCount++;
        }

        if (total == 0) return [];
        if (nonEmptyCount == 1) return single!; // avoid copying when only one list has items

        var result = new List<T>(total);
        foreach (var list in lists)
        {
            if (list.Count > 0) result.AddRange(list);
        }
        return result;
    }

    /// <summary>
    /// Detects conflicts by comparing captured OldValue with current actual value.
    /// </summary>
    internal static List<PropertyReference> DetectChangeConflicts(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        List<PropertyReference>? conflictingProperties = null;
        foreach (var change in changes)
        {
            var currentValue = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
            var capturedOldValue = change.GetOldValue<object?>();

            if (!Equals(currentValue, capturedOldValue))
            {
                conflictingProperties ??= [];
                conflictingProperties.Add(change.Property);
            }
        }
        return conflictingProperties ?? [];
    }
}
