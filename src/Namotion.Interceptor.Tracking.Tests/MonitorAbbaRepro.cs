using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Repro: in a lifecycle-free context, is the per-subject attachment monitor held across the
/// interceptor chain? If it is, two subjects cross-writing form an unordered ABBA pair with no
/// gate to serialise them.
/// </summary>
public class MonitorAbbaRepro
{
    private sealed class CrossSubjectWriter : IWriteInterceptor
    {
        public readonly Dictionary<IInterceptorSubject, (ManualResetEventSlim Entered, ManualResetEventSlim Other, Action Cross)> Routes = new();
        public volatile int Completed;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            if (context.Property.Name != "Father" || !Routes.TryGetValue(context.Property.Subject, out var route))
            {
                return;
            }

            route.Entered.Set();
            route.Other.Wait(TimeSpan.FromSeconds(10));
            route.Cross();
            Interlocked.Increment(ref Completed);
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoSubjectsCrossWriteInALifecycleFreeContext_ThenBothWritesComplete()
    {
        // Arrange
        var interceptor = new CrossSubjectWriter();
        var context = InterceptorSubjectContext.Create();
        context.AddService(interceptor);

        var s1 = new Person(context);
        var s2 = new Person(context);

        var entered1 = new ManualResetEventSlim(false);
        var entered2 = new ManualResetEventSlim(false);
        interceptor.Routes[s1] = (entered1, entered2, () => s2.Mother = new Person());
        interceptor.Routes[s2] = (entered2, entered1, () => s1.Mother = new Person());

        // Act
        var t1 = new Thread(() => s1.Father = new Person()) { IsBackground = true };
        var t2 = new Thread(() => s2.Father = new Person()) { IsBackground = true };
        t1.Start();
        t2.Start();

        var joined1 = t1.Join(TimeSpan.FromSeconds(20));
        var joined2 = t2.Join(TimeSpan.FromSeconds(20));

        // Assert
        Assert.True(joined1 && joined2,
            $"DEADLOCK. t1 joined={joined1}, t2 joined={joined2}, completed={interceptor.Completed}");
    }
}
