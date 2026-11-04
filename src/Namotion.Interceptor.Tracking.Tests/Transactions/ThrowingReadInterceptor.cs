using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Throws the given exception for every read of the property of that name while armed.
/// </summary>
internal sealed class ThrowingReadInterceptor(string propertyName, Exception exception) : IReadInterceptor
{
    public bool IsArmed { get; set; }

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        if (IsArmed && context.Property.Name == propertyName)
        {
            throw exception;
        }

        return next(ref context);
    }
}
