using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Repro: does in-chain consumer code run while the lifecycle topology gate is held?
/// If it does, two contexts whose interceptors write structurally into each other deadlock.
/// </summary>
public class GateChainDeadlockRepro
{
    private sealed class CrossContextWriter : IWriteInterceptor
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public ManualResetEventSlim? OtherEntered;
        public Action? CrossWrite;
        public volatile bool Completed;
        public volatile bool Rendezvous;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            // Only the trigger property crosses over; the cross write itself targets Mother.
            if (context.Property.Name != "Father")
            {
                return;
            }

            Entered.Set();
            Rendezvous = OtherEntered!.Wait(TimeSpan.FromSeconds(10));
            CrossWrite!();
            Completed = true;
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnInterceptorWritesIntoAnotherContextFromInsideTheChain_ThenBothWritesComplete()
    {
        // Arrange
        var interceptorA = new CrossContextWriter();
        var interceptorB = new CrossContextWriter();
        interceptorA.OtherEntered = interceptorB.Entered;
        interceptorB.OtherEntered = interceptorA.Entered;

        var contextA = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        contextA.AddService(interceptorA);
        var contextB = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        contextB.AddService(interceptorB);

        var a = new Person(contextA);
        var b = new Person(contextB);

        interceptorA.CrossWrite = () => b.Mother = new Person();
        interceptorB.CrossWrite = () => a.Mother = new Person();

        // Act
        var t1 = new Thread(() => a.Father = new Person()) { IsBackground = true };
        var t2 = new Thread(() => b.Father = new Person()) { IsBackground = true };
        t1.Start();
        t2.Start();

        var joined1 = t1.Join(TimeSpan.FromSeconds(20));
        var joined2 = t2.Join(TimeSpan.FromSeconds(20));

        // Assert
        Assert.True(joined1 && joined2,
            $"DEADLOCK. t1 joined={joined1}, t2 joined={joined2}, " +
            $"rendezvousA={interceptorA.Rendezvous}, rendezvousB={interceptorB.Rendezvous}, " +
            $"completedA={interceptorA.Completed}, completedB={interceptorB.Completed}");
    }
}
