using System.Collections;
using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

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
        var subject = new ContextProbeSubject(contextA);

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
    [InlineData(64)]
    public void WhenManyContextsFormDelegationCycle_ThenEveryResolvingOperationThrows(int cycleLength)
    {
        // Arrange
        var contexts = Enumerable
            .Range(0, cycleLength)
            .Select(_ => InterceptorSubjectContext.Create())
            .ToArray();

        var subject = new ContextProbeSubject(contexts[0]);

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
        const int chainLength = 100_000;

        var interceptor = new CountingWriteInterceptor();
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());
        rootContext.AddService<IWriteInterceptor>(interceptor);

        var deepestContext = rootContext;
        for (var index = 0; index < chainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;
        }

        var subject = new ContextProbeSubject(deepestContext);

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

        var subject = new ContextProbeSubject(contextB);

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

    [Fact]
    public void WhenServiceIsAddedToDelegationCycle_ThenResolvingSucceeds()
    {
        // Arrange
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextA);

        // Act
        contextA.AddService("test");
        var services = contextA.GetServices<string>();

        // Assert
        Assert.Contains("test", services);
    }

    [Fact]
    public void WhenTryAddServiceIsCalledOnDelegationCycle_ThenItAddsServiceAndBreaksCycle()
    {
        // Arrange
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextA);
        Assert.Throws<InvalidOperationException>(() => contextA.GetServices<string>());

        // Act
        var added = contextA.TryAddService(() => "test", _ => true);
        var services = contextA.GetServices<string>();

        // Assert
        Assert.True(added);
        Assert.Equal("test", Assert.Single(services));
    }

    [Theory]
    [InlineData(1, 1)]
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
    /// A delegation retry must re-read the entry context instead of replaying the cyclic state
    /// pinned by its caller. Replaying that stale first hop would find the same unconfirmed loop
    /// forever, while accepting the stale loop would report a cycle that no longer exists.
    /// </summary>
    [Fact]
    public async Task WhenCallerPinnedCyclicStateWasReplacedWithTerminal_ThenRetryResolvesTerminal()
    {
        // Arrange: capture an entry state that participates in a cycle, then replace its fallback
        // with a terminal context. The old first hop still appears to close a loop through entry,
        // but that loop cannot be confirmed against entry's current state.
        var entry = InterceptorSubjectContext.Create();
        var other = InterceptorSubjectContext.Create();
        var terminal = InterceptorSubjectContext.Create();
        entry.AddFallbackContext(other);
        other.AddFallbackContext(entry);

        var oldState = GetState(entry);

        entry.RemoveFallbackContext(other);
        entry.AddFallbackContext(terminal);

        var resolveMethod = typeof(InterceptorSubjectContext).GetMethod(
            "ResolveDelegationChain",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMethod);
        var arguments = new[] { oldState };

        // Act: invoking the private walk with the stale caller-pinned state directly places the
        // test at the retry boundary. Correct code re-pins the current entry state and reaches the
        // terminal. Reusing the supplied state would livelock, while accepting its stale loop would
        // throw instead of returning the terminal.
        var invocation = Task.Run(() => resolveMethod.Invoke(entry, arguments));
        await AsyncTestHelpers.WaitUntilAsync(
            () => invocation.IsCompleted,
            message: "Delegation retry reused the caller-pinned stale state and did not terminate");
        var resolved = await invocation;

        // Assert
        Assert.Same(terminal, resolved);
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

    [Fact]
    public void WhenCandidateDelegationLoopContainsReplacedState_ThenItIsNotConfirmed()
    {
        // Arrange: reproduce the candidate a concurrent walk would have collected before a
        // rewiring replaced the context state. State identity is the proof that every loop edge
        // existed at one instant, so this stale candidate must not be accepted as a real cycle.
        var context = InterceptorSubjectContext.Create();
        context.AddFallbackContext(InterceptorSubjectContext.Create());

        var oldState = GetState(context);

        var contextType = typeof(InterceptorSubjectContext);
        var hopType = contextType.GetNestedType("DelegationHop", BindingFlags.NonPublic);
        Assert.NotNull(hopType);
        var hop = Activator.CreateInstance(
            hopType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [context, oldState],
            culture: null)!;

        var path = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(hopType))!;
        path.Add(hop);

        context.AddService(new MarkerService());

        var confirmationMethod = contextType.GetMethod(
            "DelegationLoopStillClosed",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(confirmationMethod);

        // Act
        var arguments = new object?[] { path, context, null };
        var confirmed = (bool)confirmationMethod.Invoke(null, arguments)!;

        // Assert
        Assert.False(confirmed);
    }

    /// <summary>
    /// The confirmation re-reads the states of the loop and of nothing else, so only the loop may
    /// record the verdict. A context ahead of it reaches the loop according to an edge the walk
    /// read earlier, which a concurrent rewiring can have moved since, and a marker recorded there
    /// makes every query raise until a pending invalidation replaces that state.
    /// </summary>
    [Fact]
    public void WhenAcyclicPrefixLeadsIntoDelegationCycle_ThenOnlyTheCycleRecordsTheVerdict()
    {
        // Arrange: two contexts leading into a two context cycle.
        var cycleFirst = InterceptorSubjectContext.Create();
        var cycleSecond = InterceptorSubjectContext.Create();
        cycleFirst.AddFallbackContext(cycleSecond);
        cycleSecond.AddFallbackContext(cycleFirst);

        var prefixInner = InterceptorSubjectContext.Create();
        prefixInner.AddFallbackContext(cycleFirst);

        var prefixOuter = InterceptorSubjectContext.Create();
        prefixOuter.AddFallbackContext(prefixInner);

        // Act
        Assert.Throws<InvalidOperationException>(() => prefixOuter.GetServices<MarkerService>());

        // Assert: the cycle carries the verdict, the run leading into it carries nothing.
        Assert.Same(CyclicDelegationMarker, GetResolvedTerminal(cycleFirst));
        Assert.Same(CyclicDelegationMarker, GetResolvedTerminal(cycleSecond));
        Assert.Null(GetResolvedTerminal(prefixOuter));
        Assert.Null(GetResolvedTerminal(prefixInner));

        // Assert: recording nothing there means walking again, which has to reach the same verdict.
        Assert.Throws<InvalidOperationException>(() => prefixOuter.GetServices<MarkerService>());
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
        const int branchLength = 50;
        const int iterations = 25;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            // Arrange
            var terminal = InterceptorSubjectContext.Create();
            terminal.AddService(new MarkerService());

            var middle = InterceptorSubjectContext.Create();
            middle.AddFallbackContext(terminal);

            var longBranch = middle;
            for (var index = 0; index < branchLength; index++)
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
            using var readerStarted = new ManualResetEventSlim(false);
            var reader = Task.Factory.StartNew(() =>
            {
                // The window this races is the handful of invalidations between the collecting
                // context getting its final state and the branch head losing what it recorded, so
                // the reader has to be resolving already when the mutation lands. Without this the
                // detection rate is whatever the thread start happens to cost on the machine.
                collecting.GetServices<MarkerService>();
                readerStarted.Set();

                while (!Volatile.Read(ref stop))
                {
                    collecting.GetServices<MarkerService>();
                }
            }, TaskCreationOptions.LongRunning);

            readerStarted.Wait();

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
