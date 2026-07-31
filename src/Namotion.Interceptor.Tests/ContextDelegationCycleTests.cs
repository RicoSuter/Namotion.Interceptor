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
    /// The cycle lengths straddle the number of hops that are walked without cycle detection, so a
    /// regression in either the plain prefix or the detection beyond it fails this.
    /// </summary>
    [Theory]
    [InlineData(3)]
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
        // Arrange
        const int ChainLength = 500;

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
