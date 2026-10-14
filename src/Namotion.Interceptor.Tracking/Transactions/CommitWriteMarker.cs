namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Marks the write a commit is applying, or a rollback is restoring, so a transaction handler that checked the
/// final set can recognize it. Thread-static by design: the local apply and the rollback are synchronous, so the
/// mark is set and consumed within one call frame, never across an await. The first check for the marked property
/// consumes it, so a nested write of the same property, for example from a change handler, is not covered.
/// </summary>
internal static class CommitWriteMarker
{
    internal struct Frame
    {
        public bool HasValue;
        public PropertyReference Target;
        public object? Value;
        public SubjectTransaction? Transaction;
    }

    [ThreadStatic] private static Frame _frame;

    // Global gate so writes outside a commit apply pay one static read instead of a thread-static access.
    private static int _armedCount;

    internal static Scope Set(SubjectTransaction transaction, PropertyReference target, object? value)
    {
        var scope = new Scope(_frame);
        Interlocked.Increment(ref _armedCount);
        _frame = new Frame
        {
            HasValue = true,
            Target = target,
            Value = value,
            Transaction = transaction
        };
        return scope;
    }

    internal static bool TryConsume<TProperty>(ITransactionHandler handler, PropertyReference property, TProperty value)
    {
        if (Volatile.Read(ref _armedCount) == 0)
        {
            return false;
        }

        var frame = _frame;
        if (!frame.HasValue || !frame.Target.Equals(property))
        {
            return false;
        }

        _frame = default;

        // A hook that changes the value at apply time produces a value nobody checked, which must not match.
        var matches = frame.Value switch
        {
            TProperty typedValue => EqualityComparer<TProperty>.Default.Equals(typedValue, value),
            null => value is null,
            _ => false
        };

        return matches && frame.Transaction!.Handlers.Contains(handler);
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
            Interlocked.Decrement(ref _armedCount);
        }
    }
}
