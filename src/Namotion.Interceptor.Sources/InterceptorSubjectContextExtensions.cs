using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Extension methods for configuring <see cref="IInterceptorSubjectContext"/> with source-related features.
/// </summary>
public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Enables external source write support for transactions.
    /// Registers an <see cref="ITransactionWriteHandler"/> that writes changes to external sources.
    /// </summary>
    /// <param name="context">The interceptor subject context to configure.</param>
    /// <returns>The same context instance for method chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WithTransactions() or WithFullPropertyTracking() has not been called first.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Requires WithTransactions() or WithFullPropertyTracking() to be called first to register
    /// the SubjectTransactionInterceptor. This method registers a handler that writes
    /// property changes to their associated external sources (OPC UA, MQTT, etc.) before
    /// applying them to the in-process model.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var context = InterceptorSubjectContext
    ///     .Create()
    ///     .WithFullPropertyTracking()   // Registers SubjectTransactionInterceptor
    ///     .WithSourceTransactions();    // Registers ITransactionWriteHandler
    /// </code>
    /// </example>
    public static IInterceptorSubjectContext WithSourceTransactions(this IInterceptorSubjectContext context)
    {
        // Verify transaction support is enabled
        var interceptor = context.TryGetService<SubjectTransactionInterceptor>();
        if (interceptor == null)
        {
            throw new InvalidOperationException(
                "WithSourceTransactions() requires WithTransactions() or WithFullPropertyTracking() to be called first.");
        }

        // Register the source transaction write handler
        context.TryAddService<ITransactionWriteHandler>(
            () => new SourceTransactionWriteHandler(),
            _ => true);

        return context;
    }
}