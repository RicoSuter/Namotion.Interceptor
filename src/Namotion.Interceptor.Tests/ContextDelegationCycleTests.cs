using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

/// <summary>
/// A context without own services and with exactly one fallback context resolves everything through
/// that fallback. A group of such contexts that reference each other therefore has nothing to
/// resolve and no place to stop, which used to recurse until the process died on an uncatchable
/// <see cref="StackOverflowException"/>. The chain is walked iteratively now, so the depth of a
/// legitimate chain (one hop per level of the subject graph) costs no stack at all and only a real
/// cycle is reported.
/// </summary>
public class ContextDelegationCycleTests
{
    [Fact]
    public void WhenTwoContextsFormDelegationCycle_ThenEveryResolvingOperationThrows()
    {
        // Arrange: the subject is bound while the graph is still acyclic, then the second fallback
        // registration closes the cycle underneath it.
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var subject = new FuzzSubject(contextA);

        contextB.AddFallbackContext(contextA);
        contextA.AddFallbackContext(contextB);

        // Act & Assert
        var serviceQueryException = Assert.Throws<InvalidOperationException>(() => { contextA.GetServices<MarkerService>(); });
        Assert.Throws<InvalidOperationException>(() => { _ = subject.Value; });
        Assert.Throws<InvalidOperationException>(() => { subject.Value = 1; });
        Assert.Throws<InvalidOperationException>(() => { _ = subject.Echo(1); });

        Assert.Contains("delegation cycle", serviceQueryException.Message);
    }

    /// <summary>
    /// The chain is walked and reported the same way at every length, so these only guard that the
    /// walk terminates and reports on a cycle of any size rather than any particular hop count.
    /// </summary>
    [Theory]
    [InlineData(1)] // a context that is its own fallback, the shortest cycle there is
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(64)]
    public void WhenManyContextsFormDelegationCycle_ThenEveryResolvingOperationThrows(int cycleLength)
    {
        // Arrange
        var contexts = Enumerable
            .Range(0, cycleLength)
            .Select(_ => InterceptorSubjectContext.Create())
            .ToArray();

        var subject = new FuzzSubject(contexts[0]);

        for (var index = 0; index < cycleLength; index++)
        {
            contexts[index].AddFallbackContext(contexts[(index + 1) % cycleLength]);
        }

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => { contexts[0].GetServices<MarkerService>(); });
        Assert.Throws<InvalidOperationException>(() => { _ = subject.Value; });
        Assert.Throws<InvalidOperationException>(() => { subject.Value = 1; });
        Assert.Throws<InvalidOperationException>(() => { _ = subject.Echo(1); });
    }

    /// <summary>
    /// The regression guard against fixing the cycle with a hop limit: a subject graph of depth N
    /// produces a delegation chain of length N, because every attached child inherits the context of
    /// its parent as its only fallback context.
    /// </summary>
    [Fact]
    public void WhenDelegationChainIsVeryDeepWithoutCycle_ThenEveryResolvingOperationSucceeds()
    {
        // Arrange: deep enough that the recursion this replaced would die on it. That version
        // overflowed at roughly 75,000 frames on an 8 MB stack and far earlier on the 1 MB stacks
        // some hosts use, so a few hundred levels would pass either way and prove nothing.
        const int ChainLength = 100_000;

        var interceptor = new CountingWriteInterceptor();
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());
        rootContext.AddService<IWriteInterceptor>(interceptor);

        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;
        }

        var subject = new FuzzSubject(deepestContext);

        // Act
        var services = deepestContext.GetServices<MarkerService>();
        subject.Value = 42;
        var readValue = subject.Value;
        var echoedValue = subject.Echo(7);

        // Assert
        Assert.Single(services);
        Assert.Equal(42, readValue);
        Assert.Equal(7, echoedValue);
        Assert.Equal(1, interceptor.WriteCount);
    }

    [Fact]
    public void WhenRemovingFallbackContextCompletesDelegationCycle_ThenResolvingThrows()
    {
        // Arrange: two fallback contexts keep contextA from delegating, so the cycle only closes
        // when the second one is removed.
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var spareContext = InterceptorSubjectContext.Create();
        spareContext.AddService(new MarkerService());

        contextA.AddFallbackContext(contextB);
        contextA.AddFallbackContext(spareContext);
        contextB.AddFallbackContext(contextA);

        Assert.Single(contextA.GetServices<MarkerService>());
        Assert.Single(contextB.GetServices<MarkerService>());

        // Act
        contextA.RemoveFallbackContext(spareContext);

        // Assert
        Assert.Throws<InvalidOperationException>(() => { contextA.GetServices<MarkerService>(); });
        Assert.Throws<InvalidOperationException>(() => { contextB.GetServices<MarkerService>(); });
    }

    [Fact]
    public void WhenDelegationCycleIsBrokenAgain_ThenResolvingSucceeds()
    {
        // Arrange
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextB.AddFallbackContext(contextA);
        contextA.AddFallbackContext(contextB);

        Assert.Throws<InvalidOperationException>(() => { contextA.GetServices<MarkerService>(); });

        // Act: the service stops contextA from delegating, which is enough to resolve again.
        contextA.AddService(new MarkerService());

        // Assert
        Assert.Single(contextA.GetServices<MarkerService>());
        Assert.Single(contextB.GetServices<MarkerService>());
    }

    /// <summary>
    /// The pre-existing behaviour that must not change: a fallback cycle is legal and resolves
    /// normally as long as it does not consist purely of delegating contexts, which is the shape the
    /// registry produces for parent links.
    /// </summary>
    [Fact]
    public void WhenCycleContainsContextWithService_ThenResolvingSucceeds()
    {
        // Arrange
        var interceptor = new CountingWriteInterceptor();
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextA.AddService(new MarkerService());
        contextA.AddService<IWriteInterceptor>(interceptor);

        var subject = new FuzzSubject(contextB);

        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextA);

        // Act
        var servicesOfA = contextA.GetServices<MarkerService>();
        var servicesOfB = contextB.GetServices<MarkerService>();
        subject.Value = 3;

        // Assert
        Assert.Single(servicesOfA);
        Assert.Single(servicesOfB);
        Assert.Equal(3, subject.Value);
        Assert.Equal(1, interceptor.WriteCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 2)] // the cycle opens on the last unchecked hop
    [InlineData(9, 2)] // the walk is already under cycle detection when it enters the cycle
    [InlineData(20, 3)]
    public void WhenAcyclicPrefixLeadsIntoDelegationCycle_ThenResolvingThrows(int prefixLength, int cycleLength)
    {
        // Arrange: a tail that is not part of the cycle it runs into, so the repeat the walk finds
        // is not the context it started from and the reported loop is only the suffix.
        var cycle = Enumerable
            .Range(0, cycleLength)
            .Select(_ => InterceptorSubjectContext.Create())
            .ToArray();

        for (var index = 0; index < cycleLength; index++)
        {
            cycle[index].AddFallbackContext(cycle[(index + 1) % cycleLength]);
        }

        var entry = cycle[0];
        for (var index = 0; index < prefixLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(entry);
            entry = context;
        }

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => entry.GetServices<MarkerService>());
    }

    /// <summary>
    /// The verdict is cached on the state that was queried, so the second query has to reach it
    /// without walking again and report exactly the same thing.
    /// </summary>
    [Fact]
    public void WhenDelegationCycleIsQueriedRepeatedly_ThenEveryQueryThrows()
    {
        // Arrange
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var contextC = InterceptorSubjectContext.Create();
        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextC);
        contextC.AddFallbackContext(contextA);

        // Act & Assert
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => contextA.GetServices<MarkerService>());
            Assert.Contains("delegation cycle", exception.Message);
        }
    }

    /// <summary>
    /// The resolved context is cached per state, so a change that happens after a chain resolved
    /// has to invalidate that cache rather than keep answering from it. Closing the chain into a
    /// cycle is the change that turns a resolving context into a raising one.
    /// </summary>
    [Fact]
    public void WhenChainIsClosedIntoCycleAfterItResolved_ThenResolvingThrows()
    {
        // Arrange: a chain that ends on a context without services and without fallback contexts,
        // so it resolves to nothing and the resolved context gets cached on every state above it.
        var head = InterceptorSubjectContext.Create();
        var middle = InterceptorSubjectContext.Create();
        var tail = InterceptorSubjectContext.Create();
        middle.AddFallbackContext(tail);
        head.AddFallbackContext(middle);

        Assert.Empty(head.GetServices<MarkerService>());

        // Act: the tail starts delegating back to the head, which closes the chain into a cycle
        // and has to discard the resolved context cached above it.
        tail.AddFallbackContext(head);

        // Assert
        Assert.Throws<InvalidOperationException>(() => head.GetServices<MarkerService>());
        Assert.Throws<InvalidOperationException>(() => middle.GetServices<MarkerService>());
        Assert.Throws<InvalidOperationException>(() => tail.GetServices<MarkerService>());
    }

    /// <summary>
    /// The other direction: a cached cycle verdict has to be discarded when the cycle is broken.
    /// </summary>
    [Fact]
    public void WhenCycleIsBrokenAfterItWasReported_ThenResolvingSucceeds()
    {
        // Arrange
        var head = InterceptorSubjectContext.Create();
        var middle = InterceptorSubjectContext.Create();
        var answering = InterceptorSubjectContext.Create();
        answering.AddService(new MarkerService());

        head.AddFallbackContext(middle);
        middle.AddFallbackContext(head);

        Assert.Throws<InvalidOperationException>(() => head.GetServices<MarkerService>());

        // Act: a second fallback context stops the middle from delegating, so the chain ends there.
        middle.AddFallbackContext(answering);

        // Assert
        Assert.Single(head.GetServices<MarkerService>());
        Assert.Single(middle.GetServices<MarkerService>());
    }

    /// <summary>
    /// A cycle that is broken while queries are running must not leave a walker spinning. The walk
    /// starts over when the loop it found came apart underneath it, and if it started over from the
    /// state the caller pinned rather than re-reading it, a single rewiring of the queried context
    /// would make every pass replay the same stale first hop and never terminate, long after the
    /// graph stopped changing.
    /// </summary>
    [Fact]
    public async Task WhenCycleIsBrokenAndReformedWhileQueried_ThenNoQueryHangs()
    {
        // Arrange: a long cycle, so that one walk over it takes long enough for a mutation to land
        // inside it. The mutation is at the far end and only ever restores the same shape, which
        // replaces the state of every context above it, including the entry, WITHOUT changing what
        // the entry delegates to. That is the case that spins: a walk holding the entry's older
        // state keeps finding the same loop and keeps failing the same confirmation.
        const int CycleLength = 2_000;

        var cycle = Enumerable
            .Range(0, CycleLength)
            .Select(_ => InterceptorSubjectContext.Create())
            .ToArray();

        for (var index = 0; index < CycleLength; index++)
        {
            cycle[index].AddFallbackContext(cycle[(index + 1) % CycleLength]);
        }

        var entry = cycle[0];
        var last = cycle[^1];
        var stopReaders = false;
        var stopMutator = false;
        var completedQueries = 0;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!Volatile.Read(ref stopReaders))
            {
                try
                {
                    entry.GetServices<MarkerService>();
                }
                catch (InvalidOperationException)
                {
                    // The chain is a cycle almost all of the time, so raising is the expected
                    // outcome. Only a walk that never returns at all fails this test.
                }

                Interlocked.Increment(ref completedQueries);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var mutator = Task.Factory.StartNew(() =>
        {
            while (!Volatile.Read(ref stopMutator))
            {
                last.RemoveFallbackContext(entry);
                last.AddFallbackContext(entry);
            }
        }, TaskCreationOptions.LongRunning);

        // Act: the mutations stop first, so the readers are then asked to finish against a graph
        // that no longer changes. A walk that reuses the state its caller pinned does not finish
        // even then, which is what separates a livelock from ordinary contention.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Volatile.Write(ref stopMutator, true);
        await mutator;

        Volatile.Write(ref stopReaders, true);
        var allReaders = Task.WhenAll(readers);
        var finished = await Task.WhenAny(allReaders, Task.Delay(TimeSpan.FromSeconds(20))) == allReaders;

        // Assert
        Assert.True(finished,
            "A delegation walk did not return after the graph stopped changing, so it restarted forever on a " +
            $"state that no longer exists. {Volatile.Read(ref completedQueries)} queries completed.");

        Assert.True(Volatile.Read(ref completedQueries) > 0, "No query completed at all.");
    }

    /// <summary>
    /// A cycle raises for the context whose own chain it is, and contributes nothing for a context
    /// that merely reaches it as one of several fallback contexts. That difference is what lets a
    /// graph keep working when part of it is a cycle, and it has to survive the chain being
    /// resolved and cached first, which is when the cached verdict is what the walk finds.
    /// </summary>
    [Fact]
    public void WhenCollectingContextReachesDelegationCycle_ThenItResolvesItsOtherFallbackContexts()
    {
        // Arrange
        var cyclicHead = InterceptorSubjectContext.Create();
        var cyclicTail = InterceptorSubjectContext.Create();
        cyclicHead.AddFallbackContext(cyclicTail);
        cyclicTail.AddFallbackContext(cyclicHead);

        var answering = InterceptorSubjectContext.Create();
        var marker = new MarkerService();
        answering.AddService(marker);

        var collecting = InterceptorSubjectContext.Create();
        collecting.AddFallbackContext(cyclicHead);
        collecting.AddFallbackContext(answering);

        // The cycle is reported and cached first, so the collecting walk meets the cached verdict
        // rather than discovering the cycle itself.
        Assert.Throws<InvalidOperationException>(() => cyclicHead.GetServices<MarkerService>());

        // Act
        var services = collecting.GetServices<MarkerService>();

        // Assert
        Assert.Same(marker, Assert.Single(services));
    }

    /// <summary>
    /// A graph that is acyclic at every single instant must never be reported as a cycle. The walk
    /// reads one edge at a time, so it follows a path through time rather than a topology at an
    /// instant, and a sequence of rewirings that is acyclic throughout can still make it arrive at
    /// a context it already passed. That is why a repeat is confirmed before it is reported, and
    /// this is the test that a confirmation which stops being strict enough would fail.
    ///
    /// The fuzzer cannot cover this: its workers tolerate the cycle exception, because which of its
    /// contexts sit on a cycle changes with every edge they toggle. Here nothing is ever cyclic, so
    /// any exception at all is a defect.
    /// </summary>
    [Fact]
    public async Task WhenChainIsRewiredButNeverCyclic_ThenResolvingNeverReportsACycle()
    {
        // Arrange: a chain of delegating contexts ending on one that answers. The mutator moves the
        // last hop between two contexts that both lead to the answering one, so every intermediate
        // state of the graph is acyclic, while the walk keeps seeing edges change underneath it.
        const int ChainLength = 400;

        var answering = InterceptorSubjectContext.Create();
        answering.AddService(new MarkerService());

        var firstBridge = InterceptorSubjectContext.Create();
        var secondBridge = InterceptorSubjectContext.Create();
        firstBridge.AddFallbackContext(answering);
        secondBridge.AddFallbackContext(answering);

        var chain = new InterceptorSubjectContext[ChainLength];
        var deepest = firstBridge;
        for (var index = 0; index < ChainLength; index++)
        {
            chain[index] = InterceptorSubjectContext.Create();
            chain[index].AddFallbackContext(deepest);
            deepest = chain[index];
        }

        var entry = deepest;
        var swing = chain[ChainLength / 2];
        var stop = false;
        var completedQueries = 0;
        var falsePositives = 0;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    entry.GetServices<MarkerService>();
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref falsePositives);
                }

                Interlocked.Increment(ref completedQueries);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var mutator = Task.Factory.StartNew(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                // Two fallback contexts for a moment, never zero, so the chain below always
                // resolves and the graph is acyclic throughout.
                swing.AddFallbackContext(secondBridge);
                swing.RemoveFallbackContext(chain[(ChainLength / 2) - 1]);
                swing.AddFallbackContext(chain[(ChainLength / 2) - 1]);
                swing.RemoveFallbackContext(secondBridge);
            }
        }, TaskCreationOptions.LongRunning);

        // Act
        await Task.Delay(TimeSpan.FromSeconds(3));
        Volatile.Write(ref stop, true);
        await Task.WhenAll([.. readers, mutator]);

        // Assert
        Assert.True(Volatile.Read(ref completedQueries) > 0, "No query completed at all.");
        Assert.True(Volatile.Read(ref falsePositives) == 0,
            $"{Volatile.Read(ref falsePositives)} of {Volatile.Read(ref completedQueries)} queries reported a " +
            "delegation cycle on a graph that is acyclic at every instant.");
    }

    /// <summary>
    /// A context reached over the short branch of a diamond gets its final state while the long
    /// branch is still waiting to be invalidated. Anything the collecting walk believes about the
    /// long branch during that window is cached on a state that nothing invalidates again, so it
    /// has to be true of the final graph. This fails against a walk that trusts the end of a chain
    /// recorded on a state it did not re-read: the record still names a context that is a perfectly
    /// valid place to stop, it is just no longer the one that chain leads to.
    /// </summary>
    [Fact]
    public async Task WhenChainIsRewiredBelowADiamond_ThenTheCollectedServicesMatchTheFinalGraph()
    {
        // The long branch decides how many invalidations happen between the collecting context
        // getting its final state and the branch head losing what it recorded, so it is what makes
        // the window wide enough to hit.
        const int BranchLength = 50;
        const int Iterations = 400;

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            // Arrange
            var terminal = InterceptorSubjectContext.Create();
            terminal.AddService(new MarkerService());

            var middle = InterceptorSubjectContext.Create();
            middle.AddFallbackContext(terminal);

            var longBranch = middle;
            for (var index = 0; index < BranchLength; index++)
            {
                var context = InterceptorSubjectContext.Create();
                context.AddFallbackContext(longBranch);
                longBranch = context;
            }

            var shortBranch = InterceptorSubjectContext.Create();
            shortBranch.AddFallbackContext(middle);

            var collecting = InterceptorSubjectContext.Create();
            collecting.AddFallbackContext(longBranch);
            collecting.AddFallbackContext(shortBranch);

            // Resolving the branch head is what records where its chain ends, on every context of
            // the branch. Without that there is nothing stale to trust later.
            Assert.Single(longBranch.GetServices<MarkerService>());

            var stop = false;
            var reader = Task.Factory.StartNew(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    collecting.GetServices<MarkerService>();
                }
            }, TaskCreationOptions.LongRunning);

            // Act: the terminal leaves the graph, so nothing resolves a service any more.
            middle.RemoveFallbackContext(terminal);
            Volatile.Write(ref stop, true);
            await reader;

            // Assert
            Assert.True(collecting.GetServices<MarkerService>().IsEmpty,
                $"The collecting context still resolves a service of a context the graph no longer reaches, " +
                $"cached on a state that nothing invalidates again (iteration {iteration}).");
        }
    }

    private sealed class MarkerService;

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        private int _writeCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _writeCount);
            next(ref context);
        }
    }
}
