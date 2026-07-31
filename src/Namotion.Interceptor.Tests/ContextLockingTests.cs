using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests;

public class ContextLockingTests
{
    private const int Attempts = 200;
    private const int Mutations = 50;

    /// <summary>
    /// Every mutator has to release the context lock before invalidating the contexts above it,
    /// so all four are covered: a regression in any single one reintroduces the deadlock.
    /// </summary>
    [Theory]
    [InlineData(nameof(IInterceptorSubjectContext.AddService))]
    [InlineData(nameof(IInterceptorSubjectContext.TryAddService))]
    [InlineData(nameof(IInterceptorSubjectContext.AddFallbackContext))]
    [InlineData(nameof(IInterceptorSubjectContext.RemoveFallbackContext))]
    public async Task WhenFallbackContextIsMutatedWhileSubjectIsWritten_ThenNoDeadlockOccurs(string mutation)
    {
        // Arrange: the subject context keeps an own service so that it maintains an own service
        // cache and walks into the fallback context instead of delegating to it.
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var fallbackContext = InterceptorSubjectContext.Create();
            var subjectContext = InterceptorSubjectContext.Create();
            subjectContext.AddService(new MarkerService());
            subjectContext.AddFallbackContext(fallbackContext);

            var attachedContexts = Enumerable
                .Range(0, Mutations)
                .Select(_ => InterceptorSubjectContext.Create())
                .ToArray();

            if (mutation == nameof(IInterceptorSubjectContext.RemoveFallbackContext))
            {
                foreach (var attachedContext in attachedContexts)
                {
                    fallbackContext.AddFallbackContext(attachedContext);
                }
            }

            var car = new Car(subjectContext);
            using var start = new ManualResetEventSlim(false);

            var writer = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < 2_000; index++)
                {
                    car.Speed = index;
                }
            }, TaskCreationOptions.LongRunning);

            var mutator = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < Mutations; index++)
                {
                    switch (mutation)
                    {
                        case nameof(IInterceptorSubjectContext.AddService):
                            fallbackContext.AddService(new MarkerService());
                            break;

                        case nameof(IInterceptorSubjectContext.TryAddService):
                            fallbackContext.TryAddService(() => new MarkerService(), _ => false);
                            break;

                        case nameof(IInterceptorSubjectContext.AddFallbackContext):
                            fallbackContext.AddFallbackContext(attachedContexts[index]);
                            break;

                        case nameof(IInterceptorSubjectContext.RemoveFallbackContext):
                            fallbackContext.RemoveFallbackContext(attachedContexts[index]);
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
                    }
                }
            }, TaskCreationOptions.LongRunning);

            // Act
            start.Set();
            var both = Task.WhenAll(writer, mutator);
            var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15)));

            // Assert
            Assert.True(ReferenceEquals(finished, both),
                $"Deadlock on attempt {attempt} of {Attempts}: writing a property and calling {mutation} " +
                "on a fallback context acquired the two context locks in opposite orders.");
        }
    }

    [Fact]
    public async Task WhenTwoThreadsTryAddTheSameServiceOnDelegatingContext_ThenOnlyOneSucceeds()
    {
        // Arrange: a context that delegates all service lookups to a single fallback context,
        // which is the state in which the delegation fast-path field is set.
        const int ConcurrentAttempts = 3_000;
        var violations = 0;

        for (var attempt = 1; attempt <= ConcurrentAttempts; attempt++)
        {
            var fallbackContext = InterceptorSubjectContext.Create();
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(fallbackContext);

            using var start = new Barrier(2);
            var results = new bool[2];

            var adders = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryAddService(() => new MarkerService(), _ => true);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryAddService(() => new MarkerService(), _ => true);
                }, TaskCreationOptions.LongRunning)
            };

            // Act
            await Task.WhenAll(adders);

            // Assert
            if (results[0] == results[1])
            {
                violations++;
            }
        }

        Assert.True(violations == 0,
            $"TryAddService was not atomic in {violations} of {ConcurrentAttempts} attempts: two concurrent " +
            "calls for the same service type must have exactly one winner.");
    }

    [Fact]
    public async Task WhenServiceFactoryRegistersIntoSameContext_ThenNoDeadlockOccurs()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var added = false;

        // Act: the factory reenters the mutation lock of the context it is registered into.
        var work = Task.Run(() =>
        {
            added = context.TryAddService(
                () =>
                {
                    context.AddService(new OtherMarkerService());
                    return new MarkerService();
                },
                _ => true);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => work.IsCompleted,
            message: "TryAddService deadlocked while its factory mutated the same context");
        await work;

        // Assert
        Assert.True(added);
        Assert.Single(context.GetServices<MarkerService>());
        Assert.Single(context.GetServices<OtherMarkerService>());
    }

    [Fact]
    public async Task WhenFallbackGraphContainsCycle_ThenQueriesAndMutationsDoNotDeadlock()
    {
        // Arrange: both contexts keep an own service so that neither one purely delegates to
        // the other, then the fallback graph is closed into a cycle.
        const int AddedServicesPerContext = 100;

        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextA.AddService(new MarkerService());
        contextB.AddService(new MarkerService());
        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextA);

        using var start = new ManualResetEventSlim(false);

        Task StartWorker(Action work) => Task.Factory.StartNew(() =>
        {
            start.Wait();
            work();
        }, TaskCreationOptions.LongRunning);

        var workers = new[]
        {
            StartWorker(() =>
            {
                for (var index = 0; index < 1_000; index++)
                {
                    _ = contextA.GetServices<MarkerService>();
                }
            }),
            StartWorker(() =>
            {
                for (var index = 0; index < 1_000; index++)
                {
                    _ = contextB.GetServices<MarkerService>();
                }
            }),
            StartWorker(() =>
            {
                for (var index = 0; index < AddedServicesPerContext; index++)
                {
                    contextA.AddService(new MarkerService());
                }
            }),
            StartWorker(() =>
            {
                for (var index = 0; index < AddedServicesPerContext; index++)
                {
                    contextB.AddService(new MarkerService());
                }
            })
        };

        // Act
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => workers.All(worker => worker.IsCompleted),
            message: "Queries or mutations on a cyclic fallback graph deadlocked");
        await Task.WhenAll(workers);

        // Assert: both contexts aggregate every service of the whole cycle once writes settled.
        const int TotalServices = 2 + 2 * AddedServicesPerContext;
        Assert.Equal(TotalServices, contextA.GetServices<MarkerService>().Length);
        Assert.Equal(TotalServices, contextB.GetServices<MarkerService>().Length);
    }

    [Fact]
    public async Task WhenServicesAreAddedConcurrentlyWithQueries_ThenQuiescentStateSeesAllServices()
    {
        // Arrange
        const int WriterCount = 4;
        const int ReaderCount = 4;
        const int ServicesPerWriter = 50;

        var context = InterceptorSubjectContext.Create();
        using var start = new ManualResetEventSlim(false);
        var activeWriters = WriterCount;

        var writers = Enumerable.Range(0, WriterCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < ServicesPerWriter; index++)
                {
                    context.AddService(new MarkerService());
                }

                Interlocked.Decrement(ref activeWriters);
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        var readers = Enumerable.Range(0, ReaderCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();

                // Keeps refilling the service cache while writers invalidate it, so that a cache
                // entry surviving a mutation would poison the final query below.
                while (Volatile.Read(ref activeWriters) != 0)
                {
                    context.GetServices<MarkerService>();
                }
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        var workers = writers.Concat(readers).ToArray();

        // Act
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => workers.All(worker => worker.IsCompleted),
            message: "Concurrent service additions and queries did not finish");
        await Task.WhenAll(workers);

        // Assert
        Assert.Equal(WriterCount * ServicesPerWriter, context.GetServices<MarkerService>().Length);
    }

    private sealed class MarkerService;

    private sealed class OtherMarkerService;
}
