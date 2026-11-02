using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Fails the next write of a property with the given name before it lands, once armed.
/// </summary>
internal sealed class FailingWriteInterceptor(string propertyName) : IWriteInterceptor
{
    public bool IsArmed { get; set; }

    public int FailureCount { get; private set; }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (IsArmed && context.Property.Name == propertyName)
        {
            IsArmed = false;
            FailureCount++;
            throw new InvalidOperationException("The write failed.");
        }

        next(ref context);
    }
}
