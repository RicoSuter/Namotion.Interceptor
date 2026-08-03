using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Concurrency coverage against the real lifecycle stack rather than test doubles, so the
/// interaction with <see cref="LifecycleInterceptor"/> and <c>ContextInheritanceHandler</c> is
/// exercised and not just the executor in isolation.
/// </summary>
public class FallbackContextLifecycleConcurrencyTests
{
    /// <summary>
    /// Hammers add and remove of the same fallback edge against the real LifecycleInterceptor and
    /// ContextInheritanceHandler, then checks that the topology and the lifecycle bookkeeping agree.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void WhenFallbackIsToggledConcurrently_ThenBookkeepingAgreesWithTopology(int threadCount)
    {
        var failures = new List<string>();

        for (var round = 0; round < 400; round++)
        {
            var parentContext = InterceptorSubjectContext
                .Create()
                .WithContextInheritance()
                .WithLifecycle();

            var lifecycle = parentContext.GetServices<ILifecycleInterceptor>().OfType<LifecycleInterceptor>().Single();

            var attached = 0;
            var detached = 0;
            lifecycle.SubjectAttached += _ => Interlocked.Increment(ref attached);
            lifecycle.SubjectDetaching += _ => Interlocked.Increment(ref detached);

            var child = new Person { FirstName = "child", Mother = new Person { FirstName = "grandmother" } };
            var childContext = ((IInterceptorSubject)child).Context;

            using var start = new ManualResetEventSlim(false);
            var threads = new Thread[threadCount];
            for (var index = 0; index < threadCount; index++)
            {
                var isAdder = index % 2 == 0;
                threads[index] = new Thread(() =>
                {
                    start.Wait();
                    for (var operation = 0; operation < 6; operation++)
                    {
                        if (isAdder)
                        {
                            childContext.AddFallbackContext(parentContext);
                        }
                        else
                        {
                            childContext.RemoveFallbackContext(parentContext);
                        }
                    }
                });

                threads[index].Start();
            }

            start.Set();
            foreach (var thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"DEADLOCK in round {round}");
            }

            var edge = !childContext.GetServices<ILifecycleInterceptor>().IsEmpty;
            var expected = edge ? 2 : 0; // child + grandmother
            var balance = Volatile.Read(ref attached) - Volatile.Read(ref detached);
            if (balance != expected)
            {
                failures.Add($"round {round}: edge={edge} attached={attached} detached={detached} balance={balance}");
                if (failures.Count > 8) break;
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
