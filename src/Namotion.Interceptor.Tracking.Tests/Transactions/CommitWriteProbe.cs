using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Records for each write whether <see cref="SubjectTransaction.TryConsumeCommitWrite"/> consumed it for the handler.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
internal sealed class CommitWriteProbe(ITransactionHandler handler) : IWriteInterceptor
{
    public List<(string Property, object? Value, bool Consumed, bool ConsumedAgain)> Writes { get; } = [];

    public string? ThrowOnConsumedProperty { get; set; }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var consumed = SubjectTransaction.TryConsumeCommitWrite(handler, context.Property, context.NewValue);
        var consumedAgain = SubjectTransaction.TryConsumeCommitWrite(handler, context.Property, context.NewValue);
        Writes.Add((context.Property.Name, context.NewValue, consumed, consumedAgain));

        if (consumed && context.Property.Name == ThrowOnConsumedProperty)
        {
            throw new InvalidOperationException("apply failed");
        }

        next(ref context);
    }
}
