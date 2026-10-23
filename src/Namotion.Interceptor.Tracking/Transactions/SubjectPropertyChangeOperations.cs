using System.Buffers;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Operations and extension methods for <see cref="SubjectPropertyChange"/> used in transaction processing.
/// </summary>
internal static class SubjectPropertyChangeOperations
{
    private const byte NoMutation = 0;
    private const byte SuccessfulMutation = 1;
    private const byte FailedMutation = 2;

    /// <summary>
    /// Applies all changes in the span except those whose <see cref="SubjectPropertyChange.Property"/>
    /// matches a change in <paramref name="exclude"/> (matched via a <see cref="HashSet{T}"/> of excluded
    /// properties), then compensates mutations according to <paramref name="failureHandling"/> when any
    /// change was not accepted. Inspect Failed.Count == 0 to detect full success. The Successful list is
    /// returned empty only on the no-exclude full-success path, where the caller already holds the input
    /// span and does not need the applied set; with exclusions, or on any failure, Successful is populated.
    /// </summary>
    internal static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange>? exclude, TransactionFailureHandling failureHandling)
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

        return ApplyLocalChanges(changes, excluded, failureHandling);
    }

    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference>? excluded, TransactionFailureHandling failureHandling)
    {
        var rentedOutcomes = changes.Length <= 256 ? null : ArrayPool<byte>.Shared.Rent(changes.Length);
        Span<byte> outcomes = rentedOutcomes is null
            ? stackalloc byte[changes.Length]
            : rentedOutcomes.AsSpan(0, changes.Length);
        // Cleared on both paths rather than relying on localsinit to zero the stackalloc: a later
        // SkipLocalsInit would otherwise leave stale bytes here, and a stale byte makes compensation
        // issue an inverse write for a change that was never applied, silently and with no test signal.
        outcomes.Clear();

        try
        {
            return ApplyLocalChanges(changes, excluded, failureHandling, outcomes);
        }
        finally
        {
            if (rentedOutcomes is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedOutcomes);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1541", Justification = "Extracting this failure bookkeeping reproduced slower single-source commits despite unchanged allocations; keep it inline unless an alternative passes the same benchmark comparison.")]
    private static (IReadOnlyList<SubjectPropertyChange> Successful, IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors)
        ApplyLocalChanges(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference>? excluded, TransactionFailureHandling failureHandling, Span<byte> outcomes)
    {
        // When excluded is null the applied set equals the input on success, so Successful stays null
        // (returned empty) until the first failure. When excluded is set the applied set differs from the
        // input, so Successful is materialized up front.
        List<SubjectPropertyChange>? successful = excluded is null ? null : new List<SubjectPropertyChange>(changes.Length);
        List<SubjectPropertyChange>? failed = null;
        List<Exception>? errors = null;

        var outcome = new PropertyWriteOutcome();
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            if (excluded is not null && excluded.Contains(change.Property))
            {
                continue;
            }

            var accepted = change.TryApplyLocalChange(outcome, isRestore: false, out var error, out var mutated);
            outcomes[i] = ClassifyMutation(accepted, mutated);
            if (accepted)
            {
                successful?.Add(change);
                continue;
            }

            if (failed is null)
            {
                // On the no-exclude path, materialize the successful prefix at the first failure.
                successful ??= CopyAppliedChanges(changes[..i]);
                failed = [];
            }

            failed.Add(change);
            if (error is not null)
            {
                (errors ??= []).Add(error);
            }
        }

        if (failed is not null)
        {
            CompensateLocalChanges(changes, outcomes, failureHandling, outcome, ref errors);
        }

        return (successful ?? (IReadOnlyList<SubjectPropertyChange>)[],
            failed ?? (IReadOnlyList<SubjectPropertyChange>)[],
            errors ?? (IReadOnlyList<Exception>)[]);
    }

    private static byte ClassifyMutation(bool accepted, bool mutated)
    {
        if (!mutated)
        {
            return NoMutation;
        }

        return accepted ? SuccessfulMutation : FailedMutation;
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
    /// Restores mutations in the reverse of the order they were applied, so dependent properties unwind
    /// in the opposite order. Rollback restores every mutation; BestEffort restores only the mutations
    /// whose write was not accepted, leaving accepted writes standing. A compensation failure is recorded
    /// as an additional error against the change that already failed, not as a second failed change.
    /// </summary>
    private static void CompensateLocalChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        ReadOnlySpan<byte> outcomes,
        TransactionFailureHandling failureHandling,
        PropertyWriteOutcome outcome,
        ref List<Exception>? errors)
    {
        for (var index = changes.Length - 1; index >= 0; index--)
        {
            if (outcomes[index] == NoMutation ||
                (failureHandling == TransactionFailureHandling.BestEffort && outcomes[index] != FailedMutation))
            {
                continue;
            }

            if (!changes[index].ToRollbackChange().TryApplyLocalChange(outcome, isRestore: true, out var error, out _) && error is not null)
            {
                (errors ??= []).Add(error);
            }
        }
    }

    private static bool TryApplyLocalChange(this SubjectPropertyChange change, PropertyWriteOutcome outcome, bool isRestore, out Exception? error, out bool mutated)
    {
        // Assigned inside the arming scope, never from the outer exit paths: the outcome is reused across
        // the batch and still holds the previous change's flags until Arm clears them, so reading it after
        // a throw that happened before Arm would blame this change for that change's mutation.
        mutated = false;
        try
        {
            var metadata = change.Property.Metadata;
            var newValue = change.GetNewValue<object?>();
            // Arming with a Local origin is equivalent to leaving the frame unarmed, because
            // FinalizeOrigin short-circuits on Local before reading the sent value. Arming
            // unconditionally is what carries the outcome to the write, and it clears the previous result.
            using (SubjectChangeContext.WithTimestamps(change.ChangedTimestamp, change.ReceivedTimestamp))
            using (outcome.Arm(change.Property, change.Origin, newValue))
            {
                try
                {
                    metadata.SetValue?.Invoke(change.Property.Subject, newValue);
                }
                finally
                {
                    // A changed hook or observer that throws after the assignment unwinds past this scope,
                    // and its mutation must still be recorded so compensation restores it.
                    mutated = outcome.Mutated;
                }
            }

            var operation = isRestore ? "restore" : "replay";
            error = outcome.Accepted
                ? null
                : new InvalidOperationException($"Property '{change.Property.Name}' did not accept the transaction {operation}.");
            return outcome.Accepted;
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }
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
