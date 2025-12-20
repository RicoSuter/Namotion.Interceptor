using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Extension methods for configuring <see cref="IInterceptorSubjectContext"/> with source-related features.
/// </summary>
public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Enables external source write support for transactions.
    /// Registers an <see cref="ITransactionWriter"/> that writes changes to external sources.
    /// Automatically registers WithTransactions() if not already registered.
    /// </summary>
    /// <param name="context">The interceptor subject context to configure.</param>
    /// <returns>The same context instance for method chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WithTransactions() or WithFullPropertyTracking() has not been called first.
    /// </exception>
    public static IInterceptorSubjectContext WithSourceTransactions(this IInterceptorSubjectContext context)
    {
        context
            .WithTransactions()
            .TryAddService<ITransactionWriter>(
                () => new SourceTransactionWriter(),
                _ => true);

        return context;
    }
}