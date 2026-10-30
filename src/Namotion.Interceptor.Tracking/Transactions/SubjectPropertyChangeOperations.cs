using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Operations and extension methods for <see cref="SubjectPropertyChange"/> used in transaction processing.
/// </summary>
internal static class SubjectPropertyChangeOperations
{
    /// <summary>
    /// Applies the changes whose property is not in <paramref name="exclude"/>. Successful is empty on full success
    /// without exclusions, where the caller already holds the applied set, and populated otherwise. With
    /// <paramref name="markingTransaction"/>, each applied write carries that transaction's commit write mark, and
    /// applying stops at the first failure, after which that transaction reverts the set.
    /// </summary>
    internal static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange>? exclude, SubjectTransaction? markingTransaction = null)
    {
        return ApplyLocalChanges(changes, exclude is null ? null : CreatePropertySet(exclude), markingTransaction);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1541", Justification = "Extracting this failure bookkeeping reproduced slower single-source commits despite unchanged allocations; keep it inline unless an alternative passes the same benchmark comparison.")]
    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference>? excluded, SubjectTransaction? markingTransaction)
    {
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

            if (change.TryApplyLocalChange(markingTransaction, out var error))
            {
                successful?.Add(change);
            }
            else
            {
                if (failed is null)
                {
                    // On the no-exclude path, materialize the successful prefix at the first failure.
                    successful ??= CopyAppliedChanges(changes[..i]);
                    failed = [];
                }

                failed.Add(change);
                (errors ??= []).Add(error);

                // The handlers checked the changes as a set, which no longer lands as checked, so the remaining
                // changes would only run hooks and notifications against a half-applied model before the revert.
                if (markingTransaction is not null)
                {
                    break;
                }
            }
        }

        return failed is null
            ? (successful ?? (IReadOnlyList<SubjectPropertyChange>)[], [], [])
            : (successful!, failed, errors ?? []);
    }

    /// <summary>
    /// Returns the properties of the changes in <paramref name="lists"/>, or null when there are none.
    /// </summary>
    internal static HashSet<PropertyReference>? CreatePropertySet(params ReadOnlySpan<IReadOnlyList<SubjectPropertyChange>> lists)
    {
        var capacity = 0;
        foreach (var changes in lists)
        {
            capacity += changes.Count;
        }

        if (capacity == 0)
        {
            return null;
        }

        var properties = new HashSet<PropertyReference>(capacity, PropertyReference.Comparer);
        foreach (var changes in lists)
        {
            foreach (var change in changes)
            {
                properties.Add(change.Property);
            }
        }

        return properties;
    }

    private static List<SubjectPropertyChange> CopyAppliedChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        var successful = new List<SubjectPropertyChange>(changes.Length);
        for (var index = 0; index < changes.Length; index++)
        {
            successful.Add(changes[index]);
        }

        return successful;
    }

    /// <summary>
    /// Reverts applied changes in reverse order and returns the failed reverts. A failed revert reports the original
    /// change, not its rollback record, as the source-revert path does. With <paramref name="markingTransaction"/>,
    /// each restoring write carries that transaction's commit write mark.
    /// </summary>
    internal static (IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors) RevertLocalChanges(
        IReadOnlyList<SubjectPropertyChange> applied, SubjectTransaction? markingTransaction)
    {
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            var original = applied[i];
            var rollback = original.ToRollbackChange();

            if (!rollback.TryApplyLocalChange(markingTransaction, out var error))
            {
                (failed ??= []).Add(original);
                (errors ??= []).Add(error);
            }
        }

        return (failed ?? (IReadOnlyList<SubjectPropertyChange>)[], errors ?? (IReadOnlyList<Exception>)[]);
    }

    private static bool TryApplyLocalChange(
        this SubjectPropertyChange change,
        SubjectTransaction? markingTransaction,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Exception? error)
    {
        try
        {
            var newValue = change.GetNewValue<object?>();
            using (SubjectChangeContext.WithTimestamps(change.ChangedTimestamp, change.ReceivedTimestamp))
            {
                if (markingTransaction is null)
                {
                    SetLocalValue(change, newValue);
                }
                else
                {
                    using (CommitWriteMarker.Set(markingTransaction, change.Property, newValue))
                    {
                        SetLocalValue(change, newValue);
                    }
                }
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

    private static void SetLocalValue(SubjectPropertyChange change, object? newValue)
    {
        var metadata = change.Property.Metadata;
        // An absent stamp finalizes to Local like a Local one (FinalizeOrigin returns early on Local), so the hot
        // Local apply and revert skip PendingOrigin.Set. Other origins arm it so echo suppression sees the source.
        if (change.Origin.Kind == ChangeOriginKind.Local)
        {
            metadata.SetValue?.Invoke(change.Property.Subject, newValue);
        }
        else
        {
            using (PendingOrigin.Set(change.Property, change.Origin, newValue))
            {
                metadata.SetValue?.Invoke(change.Property.Subject, newValue);
            }
        }
    }

    /// <summary>
    /// Returns the changes in <paramref name="changes"/> whose property is in <paramref name="others"/> when
    /// <paramref name="keepMatches"/> is true, or not in it otherwise (matched by
    /// <see cref="SubjectPropertyChange.Property"/>).
    /// </summary>
    internal static IReadOnlyList<SubjectPropertyChange> FilterByProperty(
        IReadOnlyList<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> others,
        bool keepMatches)
    {
        if (changes.Count == 0)
        {
            return changes;
        }

        var otherProperties = CreatePropertySet(others);
        if (otherProperties is null)
        {
            return keepMatches ? [] : changes;
        }

        List<SubjectPropertyChange>? result = null;
        foreach (var change in changes)
        {
            if (otherProperties.Contains(change.Property) == keepMatches)
            {
                (result ??= []).Add(change);
            }
        }

        return result ?? (IReadOnlyList<SubjectPropertyChange>)[];
    }

    /// <summary>
    /// Returns the changes in <paramref name="changes"/> whose property is in none of <paramref name="excludedLists"/>
    /// (matched by <see cref="SubjectPropertyChange.Property"/>), as a new list.
    /// </summary>
    internal static IReadOnlyList<SubjectPropertyChange> ExcludeByProperty(
        ReadOnlySpan<SubjectPropertyChange> changes,
        params ReadOnlySpan<IReadOnlyList<SubjectPropertyChange>> excludedLists)
    {
        var excluded = CreatePropertySet(excludedLists);
        if (excluded is null)
        {
            return changes.ToArray();
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

    /// <summary>
    /// Copies the changes in <paramref name="changes"/> whose property is not in <paramref name="excludedProperties"/>
    /// to <paramref name="destination"/> in order, and returns their number.
    /// </summary>
    internal static int CopyExcludingProperties(
        ReadOnlySpan<SubjectPropertyChange> changes,
        HashSet<PropertyReference>? excludedProperties,
        Span<SubjectPropertyChange> destination)
    {
        if (excludedProperties is null)
        {
            changes.CopyTo(destination);
            return changes.Length;
        }

        var count = 0;
        foreach (var change in changes)
        {
            if (!excludedProperties.Contains(change.Property))
            {
                destination[count++] = change;
            }
        }

        return count;
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
    /// Returns the properties whose landed value differs from the captured old value, or null when there are none.
    /// </summary>
    internal static List<PropertyReference>? DetectChangeConflicts(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        List<PropertyReference>? conflictingProperties = null;
        foreach (ref readonly var change in changes)
        {
            var currentValue = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
            var capturedOldValue = change.GetOldValue<object?>();

            if (!Equals(currentValue, capturedOldValue))
            {
                conflictingProperties ??= [];
                conflictingProperties.Add(change.Property);
            }
        }
        return conflictingProperties;
    }

    /// <summary>
    /// Detects the changes whose subject's context no longer resolves <paramref name="interceptor"/>. Returns their
    /// properties, or null when there are none.
    /// </summary>
    internal static List<PropertyReference>? DetectSubjectsOutsideContext(
        ReadOnlySpan<SubjectPropertyChange> changes,
        SubjectTransactionInterceptor interceptor)
    {
        List<PropertyReference>? conflictingProperties = null;
        IInterceptorSubjectContext? resolvingContext = null;
        foreach (ref readonly var change in changes)
        {
            // A subject that left the context since the write would take the value without the context's handlers.
            var subjectContext = change.Property.Subject.TryGetContext();
            if (subjectContext is not null && ReferenceEquals(subjectContext, resolvingContext))
            {
                continue;
            }

            if (subjectContext is not null &&
                SubjectTransactionInterceptor.ResolvesInterceptor(subjectContext.GetServices<SubjectTransactionInterceptor>(), interceptor))
            {
                resolvingContext = subjectContext;
            }
            else
            {
                conflictingProperties ??= [];
                conflictingProperties.Add(change.Property);
            }
        }
        return conflictingProperties;
    }
}
