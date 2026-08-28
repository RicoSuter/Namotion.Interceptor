using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The generalized deadlock-repro shape under churn: two full lifecycle contexts whose
/// third-party interceptors write structurally into the opposite context after next(), while
/// attach and detach churn runs on both. The write path holds no lock across the chain, so
/// nothing here may deadlock, and every transient race must order instead of throwing.
/// </summary>
public class CrossContextWriteStressTests
{
    private sealed class CrossContextWriter : IWriteInterceptor
    {
        private IInterceptorSubject? _trigger;
        private Action? _crossWrite;

        public void Configure(IInterceptorSubject trigger, Action crossWrite)
        {
            _trigger = trigger;
            _crossWrite = crossWrite;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            // Only Father writes on the trigger subject cross over; the cross write itself
            // targets Mother, so it cannot recurse.
            if (context.IsWritten &&
                context.Property.Name == nameof(Person.Father) &&
                ReferenceEquals(context.Property.Subject, _trigger))
            {
                _crossWrite!();
            }
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoLifecycleContextsCrossWriteUnderChurn_ThenEverythingCompletesAndTheGraphsSettleConsistently()
    {
        // Arrange
        var interceptorA = new CrossContextWriter();
        var interceptorB = new CrossContextWriter();
        var contextA = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        contextA.AddService<IWriteInterceptor>(interceptorA);
        var contextB = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        contextB.AddService<IWriteInterceptor>(interceptorB);

        var a = new Person(contextA) { FirstName = "A" };
        var b = new Person(contextB) { FirstName = "B" };
        interceptorA.Configure(a, () => b.Mother = new Person());
        interceptorB.Configure(b, () => a.Mother = new Person());

        const int iterations = 1000;
        var exceptions = new List<Exception>();
        var progress = new int[4];
        var barrier = new Barrier(4);

        Thread StartLoop(int slot, Action<int, Random> body)
        {
            var thread = new Thread(() =>
            {
                var random = new Random(42 + slot);
                barrier.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    try
                    {
                        body(i, random);
                    }
                    catch (Exception exception)
                    {
                        lock (exceptions)
                        {
                            exceptions.Add(exception);
                        }
                    }

                    Volatile.Write(ref progress[slot], i + 1);
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        var churnA = new Person { FirstName = "CA" };
        var churnB = new Person { FirstName = "CB" };

        // Act
        var threads = new[]
        {
            StartLoop(0, (_, random) => a.Father = random.Next(2) == 0 ? new Person() : null),
            StartLoop(1, (_, random) => b.Father = random.Next(2) == 0 ? new Person() : null),
            StartLoop(2, (_, _) =>
            {
                churnA.AttachToContext(contextA);
                churnA.DetachFromContext(contextA);
            }),
            StartLoop(3, (_, _) =>
            {
                churnB.AttachToContext(contextB);
                churnB.DetachFromContext(contextB);
            })
        };

        var completed = true;
        foreach (var thread in threads)
        {
            completed &= thread.Join(TimeSpan.FromSeconds(30));
        }

        // Assert: completion first, so a lock scope defect fails as a timeout instead of hanging.
        Assert.True(completed,
            "probable deadlock: progress " +
            $"writerA={Volatile.Read(ref progress[0])}, writerB={Volatile.Read(ref progress[1])}, " +
            $"churnA={Volatile.Read(ref progress[2])}, churnB={Volatile.Read(ref progress[3])} of {iterations}");
        Assert.Empty(exceptions);

        // The settled graphs are consistent and still track new writes.
        Assert.Same(contextA, a.TryGetContext());
        Assert.Same(contextB, b.TryGetContext());
        var settledFather = new Person { FirstName = "S" };
        a.Father = settledFather;
        Assert.Same(contextA, settledFather.TryGetContext());
        Assert.Equal(1, settledFather.GetReferenceCount());
    }
}
