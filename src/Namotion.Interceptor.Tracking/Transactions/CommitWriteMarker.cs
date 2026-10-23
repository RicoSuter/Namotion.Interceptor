using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Marks the write a commit is applying, or a rollback is restoring, for
/// <see cref="SubjectTransaction.TryConsumeCommitWrite"/>. Nested writes (change handlers, derived recalculations)
/// are not covered.
/// </summary>
internal static class CommitWriteMarker
{
    internal struct Frame
    {
        public PropertyReference Target;
        public object? Value;
        public SubjectTransaction? Transaction;
    }

    // Thread-static: the local apply and the rollback are synchronous, so the mark is set and consumed within one
    // call frame, never across an await. Same-property re-entry before the consumer runs (an OnChanging hook or an
    // earlier interceptor) takes the mark instead, the same limitation as PendingOrigin.
    [ThreadStatic] private static Frame _frame;

    internal static Scope Set(SubjectTransaction transaction, PropertyReference target, object? value)
    {
        var scope = new Scope(_frame);
        _frame = new Frame
        {
            Target = target,
            Value = value,
            Transaction = transaction
        };
        return scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryConsume<TProperty>(ITransactionHandler handler, PropertyReference property, TProperty value)
    {
        // A dispose mid-commit from another flow can clear this gate while a mark is set; the slow path rejects the
        // mark of a disposed transaction too, so the handler checks the write either way.
        return SubjectTransaction.HasActiveTransaction && TryConsumeMarked(handler, property, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryConsumeMarked<TProperty>(ITransactionHandler handler, PropertyReference property, TProperty value)
    {
        var frame = _frame;
        if (frame.Transaction is null ||
            !frame.Target.Equals(property) ||
            frame.Transaction.IsDisposed ||
            !frame.Transaction.Handlers.Contains(handler))
        {
            return false;
        }

        _frame = default;

        // References match by identity: the apply passes the marked instance itself, and a loose Equals must not let
        // a replaced, unchecked instance pass as checked.
        if (!typeof(TProperty).IsValueType)
        {
            return ReferenceEquals(frame.Value, value);
        }

        if (frame.Value is null)
        {
            return value is null;
        }

        return frame.Value is TProperty typedValue && EqualityComparer<TProperty>.Default.Equals(typedValue, value);
    }

    internal readonly ref struct Scope
    {
        private readonly Frame _previousFrame;

        internal Scope(in Frame previousFrame)
        {
            _previousFrame = previousFrame;
        }

        public void Dispose()
        {
            _frame = _previousFrame;
        }
    }
}
