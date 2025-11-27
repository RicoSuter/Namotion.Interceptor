using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Extension methods for configuring <see cref="IInterceptorSubjectContext"/> with source-related features.
/// </summary>
public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Enables external source write support for transactions.
    /// Sets the WriteChangesCallback on the SubjectTransactionInterceptor.
    /// </summary>
    /// <param name="context">The interceptor subject context to configure.</param>
    /// <returns>The same context instance for method chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WithTransactions() or WithFullPropertyTracking() has not been called first.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Requires WithTransactions() or WithFullPropertyTracking() to be called first to register
    /// the SubjectTransactionInterceptor. This method configures the interceptor to write
    /// property changes to their associated external sources (OPC UA, MQTT, etc.) before
    /// applying them to the in-process model.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var context = InterceptorSubjectContext
    ///     .Create()
    ///     .WithFullPropertyTracking()   // Registers SubjectTransactionInterceptor
    ///     .WithSourceTransactions();    // Sets up external source writes
    /// </code>
    /// </example>
    public static IInterceptorSubjectContext WithSourceTransactions(this IInterceptorSubjectContext context)
    {
        // Get the transaction interceptor from context
        var interceptor = context.TryGetService<SubjectTransactionInterceptor>();
        if (interceptor == null)
        {
            throw new InvalidOperationException(
                "WithSourceTransactions() requires WithTransactions() or WithFullPropertyTracking() to be called first.");
        }

        // Set the write callback to handle external source writes
        interceptor.WriteChangesCallback = WriteChangesToSourcesAsync;

        return context;
    }

    private static async Task<TransactionWriteResult> WriteChangesToSourcesAsync(
        IReadOnlyList<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        // 1. Group changes by source
        var changesBySource = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();
        var changesWithoutSource = new List<SubjectPropertyChange>();

        foreach (var change in changes)
        {
            if (change.Property.TryGetSource(out var source))
            {
                if (!changesBySource.TryGetValue(source, out var list))
                {
                    list = [];
                    changesBySource[source] = list;
                }
                list.Add(change);
            }
            else
            {
                changesWithoutSource.Add(change);
            }
        }

        // 2. Write to each source (continue all, report errors)
        var successfulChanges = new List<SubjectPropertyChange>(changesWithoutSource);
        var failures = new List<Exception>();

        foreach (var (source, sourceChanges) in changesBySource)
        {
            try
            {
                var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
                await source.WriteChangesInBatchesAsync(memory, cancellationToken);
                successfulChanges.AddRange(sourceChanges);
            }
            catch (Exception ex)
            {
                failures.Add(new SourceWriteException(source, sourceChanges, ex));
            }
        }

        return new TransactionWriteResult(successfulChanges, failures);
    }
}
