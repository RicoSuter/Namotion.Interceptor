using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Parks one write of one value to one property, the first by default, before PropertyChangeInterceptor publishes it.
[RunsBefore(typeof(PropertyChangeInterceptor))]
internal sealed class ValueBlockingWriteInterceptor(string propertyName, object blockedValue, int occurrence = 1) : IWriteInterceptor
{
    private int _matchCount;

    public ManualResetEventSlim Entered { get; } = new(false);

    public ManualResetEventSlim Proceed { get; } = new(false);

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (context.Property.Name == propertyName &&
            Equals(context.NewValue, blockedValue) &&
            Interlocked.Increment(ref _matchCount) == occurrence)
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
