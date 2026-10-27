using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// The transaction handlers that take part in a transaction: those of the transaction's context, resolved when it
/// begins (after any wait for the exclusive lock), followed by those added from the contexts of written subjects.
/// Handlers match by equality.
/// </summary>
// A mutable struct embedded in the transaction to save an allocation per transaction. It must only be used through
// the transaction's field: a copy, or a readonly field, would publish inclusions to the copy.
internal struct TransactionHandlerSet
{
    // Replaced under the transaction lock only while the transaction captures, so the handlers that checked a commit
    // are exactly the ones its applies are marked for. Never mutated in place: it starts as the context's cached
    // service array.
    private ITransactionHandler[] _handlers;

    // Caches only (a stale read just takes the slow path): the transaction interceptors of the last subject context
    // included, and the last handler array included. Written after _handlers with release semantics, so a reader that
    // skips on a hit also sees the included handlers. The interceptor array identifies the subject context's handlers
    // (see IInterceptorSubjectContext.GetServices) and is never empty: it holds the transaction's interceptor.
    private SubjectTransactionInterceptor[]? _lastIncludedInterceptors;
    private ITransactionHandler[] _lastIncludedHandlers;

    /// <summary>
    /// Initializes the set with <paramref name="transactionContextHandlers"/>, the transaction's context handlers.
    /// </summary>
    internal TransactionHandlerSet(ImmutableArray<ITransactionHandler> transactionContextHandlers)
    {
        _handlers = ImmutableCollectionsMarshal.AsArray(transactionContextHandlers) ?? [];
        _lastIncludedHandlers = _handlers;
    }

    /// <summary>
    /// Gets the handlers, in the order they were resolved. The array is never mutated.
    /// </summary>
    internal ITransactionHandler[] Handlers => Volatile.Read(ref _handlers);

    /// <summary>
    /// Adds the handlers of <paramref name="subjectContext"/> that are not registered on the transaction's context.
    /// Returns false when handlers had to be added but were not, because the transaction no longer captures or, with
    /// <paramref name="skipWhileCommitting"/>, a commit is in progress.
    /// </summary>
    /// <param name="subjectContext">The context of the written subject.</param>
    /// <param name="subjectInterceptors">
    /// The transaction interceptors <paramref name="subjectContext"/> resolves, as returned by its
    /// <see cref="IInterceptorSubjectContext.GetServices{TInterface}"/>, including the transaction's interceptor.
    /// </param>
    /// <param name="transaction">The transaction the set belongs to.</param>
    /// <param name="skipWhileCommitting">
    /// True to add nothing instead of throwing while a commit is in progress, for a write that is dropped then.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown by <see cref="SubjectTransaction.CanCaptureUnderLock"/> while a commit is in progress and
    /// <paramref name="subjectContext"/> adds handlers, unless <paramref name="skipWhileCommitting"/> is true.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Include(
        IInterceptorSubjectContext subjectContext,
        ImmutableArray<SubjectTransactionInterceptor> subjectInterceptors,
        SubjectTransaction transaction,
        bool skipWhileCommitting = false)
    {
        var interceptors = ImmutableCollectionsMarshal.AsArray(subjectInterceptors)!;
        return ReferenceEquals(interceptors, Volatile.Read(ref _lastIncludedInterceptors)) ||
            IncludeUncached(subjectContext, interceptors, transaction, skipWhileCommitting);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool IncludeUncached(
        IInterceptorSubjectContext subjectContext,
        SubjectTransactionInterceptor[] subjectInterceptors,
        SubjectTransaction transaction,
        bool skipWhileCommitting)
    {
        var transactionContext = transaction.Context;
        if (!ReferenceEquals(subjectContext, transactionContext))
        {
            var subjectHandlers = ImmutableCollectionsMarshal.AsArray(subjectContext.GetServices<ITransactionHandler>()) ?? [];
            if (!ReferenceEquals(subjectHandlers, Volatile.Read(ref _lastIncludedHandlers)))
            {
                if (!TryInclude(subjectHandlers, transactionContext.GetServices<ITransactionHandler>().AsSpan(), transaction, skipWhileCommitting))
                {
                    return false;
                }

                Volatile.Write(ref _lastIncludedHandlers, subjectHandlers);
            }
        }

        Volatile.Write(ref _lastIncludedInterceptors, subjectInterceptors);
        return true;
    }

    /// <summary>
    /// Adds the handlers of <paramref name="subjectHandlers"/> that are in neither the set nor
    /// <paramref name="transactionContextHandlers"/>, so a handler registered on the transaction's context after it
    /// began stays out. Returns false when there are additions and the transaction no longer captures, or a commit is
    /// in progress and <paramref name="skipWhileCommitting"/> is true.
    /// </summary>
    private bool TryInclude(
        ReadOnlySpan<ITransactionHandler> subjectHandlers,
        ReadOnlySpan<ITransactionHandler> transactionContextHandlers,
        SubjectTransaction transaction,
        bool skipWhileCommitting)
    {
        while (true)
        {
            // Handler equality can run user code, so the union is built before taking the transaction lock and
            // published only when no concurrent inclusion replaced the handlers in between.
            var handlers = Volatile.Read(ref _handlers);
            var union = CreateUnion(handlers, subjectHandlers, transactionContextHandlers);
            if (union is null)
            {
                // Inclusions only append, so every later set holds these handlers too.
                return true;
            }

            lock (transaction.PendingChangesLock)
            {
                if ((skipWhileCommitting && transaction.IsCommitting) || !transaction.CanCaptureUnderLock())
                {
                    return false;
                }

                if (ReferenceEquals(_handlers, handlers))
                {
                    Volatile.Write(ref _handlers, union);
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
