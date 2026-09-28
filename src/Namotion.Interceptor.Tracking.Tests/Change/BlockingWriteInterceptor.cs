using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Parks the writer between PropertyChangeInterceptor's pre-commit work and the terminal commit.
[RunsAfter(typeof(PropertyChangeInterceptor))]
internal sealed class BlockingWriteInterceptor : IWriteInterceptor
{
    public ManualResetEventSlim EnteredInnerChain { get; } = new(false);
    public ManualResetEventSlim ProceedWithCommit { get; } = new(false);

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        EnteredInnerChain.Set();
        if (!ProceedWithCommit.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The test did not release the blocked write within 10 seconds.");
        }

        next(ref context);
    }
}
