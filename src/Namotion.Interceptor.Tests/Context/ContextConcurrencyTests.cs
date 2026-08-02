using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

public class ContextConcurrencyTests
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

            // Act & Assert
            start.Set();
            var both = Task.WhenAll(writer, mutator);
            try
            {
                await both.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Deadlock on attempt {attempt} of {Attempts}: writing a property and calling {mutation} " +
                    "on a fallback context acquired the two context locks in opposite orders.",
                    exception);
            }
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

            // Act: bounded so that a deadlock regression fails the run instead of hanging it
            // without a message. Awaited rather than polled, because this runs thousands of times
            // and a poll interval would dominate the whole test.
            try
            {
                await Task.WhenAll(adders).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Two concurrent TryAddService calls deadlocked on attempt {attempt} of {ConcurrentAttempts}.",
                    exception);
            }

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
    public async Task WhenServiceExistsPredicateRegistersIntoSameContext_ThenNoDeadlockOccurs()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new MarkerService());
        var predicateRan = false;
        var added = false;

        // Act: the predicate reenters the mutation lock of the context it is querying.
        var work = Task.Run(() =>
        {
            added = context.TryAddService(
                () => new MarkerService(),
                _ =>
                {
                    predicateRan = true;
                    context.AddService(new OtherMarkerService());
                    return false;
                });
        });

        await AsyncTestHelpers.WaitUntilAsync(() => work.IsCompleted,
            message: "TryAddService deadlocked while its exists predicate mutated the same context");
        await work;

        // Assert
        Assert.True(predicateRan);
        Assert.True(added);
        Assert.Equal(2, context.GetServices<MarkerService>().Length);
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

    [Fact]
    public async Task WhenTwoContextsAddEachOtherAsFallbackConcurrently_ThenNoDeadlockOccurs()
    {
        // Arrange: both contexts keep an own service so that neither one purely delegates to the
        // other. Each thread mutates its own context and registers into the other one, which is
        // the interleaving that a per-context using-set lock has to survive.
        const int MutualRegistrations = 2_000;

        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextA.AddService(new MarkerService());
        contextB.AddService(new MarkerService());

        using var start = new Barrier(2);

        Task StartMutator(InterceptorSubjectContext context, InterceptorSubjectContext other) =>
            Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                for (var index = 0; index < MutualRegistrations; index++)
                {
                    context.AddFallbackContext(other);
                    context.RemoveFallbackContext(other);
                }
            }, TaskCreationOptions.LongRunning);

        var mutators = new[]
        {
            StartMutator(contextA, contextB),
            StartMutator(contextB, contextA)
        };

        // Act
        await AsyncTestHelpers.WaitUntilAsync(() => mutators.All(mutator => mutator.IsCompleted),
            message: "Two contexts registering into each other concurrently deadlocked");
        await Task.WhenAll(mutators);

        // Assert: every registration was undone again, and a fresh mutual registration is still
        // observed on both sides, so no add or remove was lost to the concurrent set access.
        Assert.Single(contextA.GetServices<MarkerService>());
        Assert.Single(contextB.GetServices<MarkerService>());

        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextA);

        Assert.Equal(2, contextA.GetServices<MarkerService>().Length);
        Assert.Equal(2, contextB.GetServices<MarkerService>().Length);
    }

    [Fact]
    public async Task WhenSameFallbackIsAddedAndRemovedConcurrently_ThenFinalTopologyAndInvalidationAgree()
    {
        // Arrange: own services keep the source context from delegating, so it owns a service cache
        // whose invalidation also checks the reverse edge maintained by add and remove.
        const int mutationsPerWorker = 2_000;

        var context = InterceptorSubjectContext.Create();
        context.AddService(new MarkerService());

        var fallbackContext = InterceptorSubjectContext.Create();
        fallbackContext.AddService(new MarkerService());

        using var start = new Barrier(2);
        var successfulAdds = 0;
        var successfulRemoves = 0;

        var adder = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var index = 0; index < mutationsPerWorker; index++)
            {
                if (context.AddFallbackContext(fallbackContext))
                {
                    successfulAdds++;
                }
            }
        }, TaskCreationOptions.LongRunning);

        var remover = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var index = 0; index < mutationsPerWorker; index++)
            {
                if (context.RemoveFallbackContext(fallbackContext))
                {
                    successfulRemoves++;
                }
            }
        }, TaskCreationOptions.LongRunning);

        // Act
        var workers = new[] { adder, remover };
        await AsyncTestHelpers.WaitUntilAsync(() => workers.All(worker => worker.IsCompleted),
            message: "Concurrent add and remove of the same fallback context did not finish");
        await Task.WhenAll(workers);

        var fallbackIsPresent = successfulAdds == successfulRemoves + 1;
        var servicesBeforeMutation = context.GetServices<MarkerService>();
        fallbackContext.AddService(new MarkerService());
        var servicesAfterMutation = context.GetServices<MarkerService>();

        // Assert: successful transitions determine the final edge exactly. If the edge remains,
        // the fallback mutation must also invalidate the source cache through its reverse edge.
        Assert.True(successfulAdds == successfulRemoves || fallbackIsPresent);
        Assert.Equal(fallbackIsPresent ? 2 : 1, servicesBeforeMutation.Length);
        Assert.Equal(fallbackIsPresent ? 3 : 1, servicesAfterMutation.Length);
    }

    [Fact]
    public async Task WhenManyContextsAddTheSameFallbackConcurrently_ThenAllSeeLaterTopologyChanges()
    {
        // Arrange: the fan-in shape, all children register into the using set of one parent.
        const int ChildCount = 32;

        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService(new MarkerService());

        var childContexts = Enumerable
            .Range(0, ChildCount)
            .Select(_ =>
            {
                // The own service keeps the child from delegating to the parent, so it maintains
                // an own service cache that the parent has to invalidate.
                var childContext = InterceptorSubjectContext.Create();
                childContext.AddService(new MarkerService());
                return childContext;
            })
            .ToArray();

        using var start = new Barrier(ChildCount);

        var registrations = childContexts
            .Select(childContext => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                childContext.AddFallbackContext(parentContext);

                // Fills the child service cache so that an entry surviving the topology change
                // below would hide it.
                childContext.GetServices<MarkerService>();
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        // Act: the barrier releases as soon as all registering threads arrived.
        await AsyncTestHelpers.WaitUntilAsync(() => registrations.All(registration => registration.IsCompleted),
            message: "Concurrent registrations into one shared parent context did not finish");
        await Task.WhenAll(registrations);

        Assert.All(childContexts, childContext => Assert.Equal(2, childContext.GetServices<MarkerService>().Length));

        parentContext.AddService(new MarkerService());

        // Assert: every child was registered, so every child observes the new parent topology.
        Assert.All(childContexts, childContext => Assert.Equal(3, childContext.GetServices<MarkerService>().Length));
    }

    [Fact]
    public async Task WhenFallbackIsRemovedWhileInvalidationWalksTheSameSet_ThenNoInvalidationIsLost()
    {
        // Arrange: the writer invalidates through the using set of the parent context while the
        // churning child adds and removes itself from that very set.
        const int StableChildCount = 8;
        const int AddedServices = 200;
        const int ChurnIterations = 200;

        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService(new MarkerService());

        var childContexts = Enumerable
            .Range(0, StableChildCount + 1)
            .Select(_ =>
            {
                var childContext = InterceptorSubjectContext.Create();
                childContext.AddService(new MarkerService());
                childContext.AddFallbackContext(parentContext);
                return childContext;
            })
            .ToArray();

        var churningChildContext = childContexts[^1];

        using var start = new ManualResetEventSlim(false);
        var activeWriters = 1;

        var writer = Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (var index = 0; index < AddedServices; index++)
            {
                parentContext.AddService(new MarkerService());
            }

            Interlocked.Decrement(ref activeWriters);
        }, TaskCreationOptions.LongRunning);

        var churner = Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (var index = 0; index < ChurnIterations; index++)
            {
                churningChildContext.RemoveFallbackContext(parentContext);
                churningChildContext.AddFallbackContext(parentContext);
            }
        }, TaskCreationOptions.LongRunning);

        var readers = childContexts
            .Select(childContext => Task.Factory.StartNew(() =>
            {
                start.Wait();

                // Keeps refilling the child caches while the writer invalidates them, so that an
                // entry surviving a mutation would poison the final queries below.
                while (Volatile.Read(ref activeWriters) != 0)
                {
                    childContext.GetServices<MarkerService>();
                }
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        var workers = new[] { writer, churner }.Concat(readers).ToArray();

        // Act
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => workers.All(worker => worker.IsCompleted),
            message: "Removing a fallback while an invalidation walked the same set deadlocked");
        await Task.WhenAll(workers);

        // Assert: the churn ends on a registration, so every child resolves its own service plus
        // every service of the parent context.
        const int TotalServicesPerChild = 1 + 1 + AddedServices;
        Assert.All(childContexts, childContext => Assert.Equal(TotalServicesPerChild, childContext.GetServices<MarkerService>().Length));
    }

    private sealed class MarkerService;

    private sealed class OtherMarkerService;
}
