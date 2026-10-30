using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Throws for every write of a configured value to the property of that name. Runs after the transaction
/// interceptor, so it sees only the writes that reach the model, such as the local apply and revert of a commit.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
internal sealed class ValueRejectingWriteInterceptor : IWriteInterceptor
{
    public HashSet<(string Property, object? Value)> Rejected { get; } = [];

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (Rejected.Contains((context.Property.Name, context.NewValue)))
        {
            throw new InvalidOperationException($"{context.Property.Name} write failed.");
        }

        next(ref context);
    }
}
