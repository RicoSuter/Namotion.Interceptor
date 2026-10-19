using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// The transaction handlers that take part in a transaction: those of the transaction's context, resolved when it
/// begins, followed by those added from the contexts of written subjects. Handlers match by equality.
/// </summary>
// A mutable struct embedded in the transaction to save an allocation per transaction. It must only be used through
// the transaction's field: a copy, or a readonly field, would publish inclusions to the copy.
internal struct TransactionHandlerSet
{
    // Replaced under the transaction lock only while the transaction captures, so the handlers that checked a commit
    // are exactly the ones its applies are marked for. Never mutated in place: it starts as the context's cached
    // service array.
    private ITransactionHandler[] _handlers;

    // Caches only (a stale read just takes the slow path): the last subject context and handler array included.
    // Written after _handlers with release semantics, so a reader that skips on a hit also sees the included handlers.
    private IInterceptorSubjectContext? _lastIncludedContext;
    private ITransactionHandler[] _lastIncludedHandlers;

    /// <summary>
    /// Initializes the set with the handlers of the transaction's context.
    /// </summary>
    internal TransactionHandlerSet(IInterceptorSubjectContext transactionContext)
    {
        _handlers = ImmutableCollectionsMarshal.AsArray(transactionContext.GetServices<ITransactionHandler>()) ?? [];
        _lastIncludedHandlers = _handlers;
    }

    /// <summary>
    /// Gets the handlers, in the order they were resolved. The array is never mutated.
    /// </summary>
    internal ITransactionHandler[] Handlers => Volatile.Read(ref _handlers);

    /// <summary>
    /// Adds the handlers of <paramref name="subjectContext"/> that are not registered on the transaction's context.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown by <see cref="SubjectTransaction.CanCaptureUnderLock"/> while a commit is in progress.
    /// </exception>
    internal void Include(IInterceptorSubjectContext subjectContext, SubjectTransaction transaction, Lock transactionLock)
    {
        var transactionContext = transaction.Context;
        if (ReferenceEquals(subjectContext, transactionContext) || ReferenceEquals(subjectContext, Volatile.Read(ref _lastIncludedContext)))
        {
            return;
        }

        var subjectHandlers = ImmutableCollectionsMarshal.AsArray(subjectContext.GetServices<ITransactionHandler>()) ?? [];
        if (!ReferenceEquals(subjectHandlers, Volatile.Read(ref _lastIncludedHandlers)))
        {
            if (!ContainsAll(Volatile.Read(ref _handlers), subjectHandlers) &&
                !TryInclude(subjectHandlers, transactionContext.GetServices<ITransactionHandler>().AsSpan(), transaction, transactionLock))
            {
                return;
            }

            Volatile.Write(ref _lastIncludedHandlers, subjectHandlers);
        }

        Volatile.Write(ref _lastIncludedContext, subjectContext);
    }

    /// <summary>
    /// Adds the handlers of <paramref name="subjectHandlers"/> that are in neither the set nor
    /// <paramref name="transactionContextHandlers"/>, so a handler registered on the transaction's context after it
    /// began stays out. Returns false when the transaction no longer captures.
    /// </summary>
    private bool TryInclude(
        ReadOnlySpan<ITransactionHandler> subjectHandlers,
        ReadOnlySpan<ITransactionHandler> transactionContextHandlers,
        SubjectTransaction transaction,
        Lock transactionLock)
    {
        while (true)
        {
            // Handler equality can run user code, so the union is built before taking the transaction lock and
            // published only when no concurrent inclusion replaced the handlers in between.
            var handlers = Volatile.Read(ref _handlers);
            var union = CreateUnion(handlers, subjectHandlers, transactionContextHandlers);
            lock (transactionLock)
            {
                if (!transaction.CanCaptureUnderLock())
                {
                    return false;
                }

                if (ReferenceEquals(_handlers, handlers))
                {
                    if (union is not null)
                    {
                        Volatile.Write(ref _handlers, union);
                    }

                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="handlers"/> followed by the additions of <paramref name="subjectHandlers"/>, or null
    /// when there are none.
    /// </summary>
    private static ITransactionHandler[]? CreateUnion(
        ITransactionHandler[] handlers,
        ReadOnlySpan<ITransactionHandler> subjectHandlers,
        ReadOnlySpan<ITransactionHandler> transactionContextHandlers)
    {
        var additionCount = 0;
        for (var index = 0; index < subjectHandlers.Length; index++)
        {
            if (IsAddition(subjectHandlers, index, handlers, transactionContextHandlers))
            {
                additionCount++;
            }
        }

        if (additionCount == 0)
        {
            return null;
        }

        var union = new ITransactionHandler[handlers.Length + additionCount];
        handlers.CopyTo(union, 0);
        var count = handlers.Length;
        for (var index = 0; index < subjectHandlers.Length; index++)
        {
            if (IsAddition(subjectHandlers, index, handlers, transactionContextHandlers))
            {
                union[count++] = subjectHandlers[index];
            }
        }

        return union;
    }

    private static bool IsAddition(
        ReadOnlySpan<ITransactionHandler> subjectHandlers,
        int index,
        ReadOnlySpan<ITransactionHandler> handlers,
        ReadOnlySpan<ITransactionHandler> transactionContextHandlers)
    {
        var handler = subjectHandlers[index];
        return !Contains(handlers, handler) &&
            !Contains(transactionContextHandlers, handler) &&
            !Contains(subjectHandlers[..index], handler);
    }

    private static bool ContainsAll(ReadOnlySpan<ITransactionHandler> handlers, ReadOnlySpan<ITransactionHandler> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!Contains(handlers, candidate))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(ReadOnlySpan<ITransactionHandler> handlers, ITransactionHandler handler)
    {
        foreach (var candidate in handlers)
        {
            if (candidate.Equals(handler))
            {
                return true;
            }
        }

        return false;
    }
}
