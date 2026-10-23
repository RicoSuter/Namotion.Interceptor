using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Parks the first write of one value to one property before PropertyChangeInterceptor publishes it.
[RunsBefore(typeof(PropertyChangeInterceptor))]
internal sealed class ValueBlockingWriteInterceptor(string propertyName, object blockedValue) : IWriteInterceptor
{
    private int _hasBlocked;

    public ManualResetEventSlim Entered { get; } = new(false);

    public ManualResetEventSlim Proceed { get; } = new(false);

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (context.Property.Name == propertyName &&
            Equals(context.NewValue, blockedValue) &&
            Interlocked.Exchange(ref _hasBlocked, 1) == 0)
        {
            Entered.Set();
            if (!Proceed.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the blocked write within 10 seconds.");
            }
        }

        next(ref context);
    }
}
