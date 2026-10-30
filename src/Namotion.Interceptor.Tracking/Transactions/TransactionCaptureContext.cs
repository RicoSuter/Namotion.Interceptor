using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// The write a transaction is about to capture, passed to <see cref="ITransactionHandler.OnCapturing{TProperty}"/>.
/// Valid only during that call.
/// </summary>
/// <typeparam name="TProperty">The property type.</typeparam>
public readonly ref struct TransactionCaptureContext<TProperty>
{
    internal TransactionCaptureContext(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
    {
        Transaction = transaction;
        Property = property;
        NewValue = newValue;
        Origin = origin;
    }

    /// <summary>Gets the capturing transaction.</summary>
    public SubjectTransaction Transaction { get; }

    /// <summary>Gets the written property.</summary>
    public PropertyReference Property { get; }

    /// <summary>Gets the written value.</summary>
    public TProperty NewValue { get; }

    /// <summary>Gets the attempted origin of the write.</summary>
    public ChangeOrigin Origin { get; }
}
