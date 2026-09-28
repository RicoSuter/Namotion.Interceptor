using System.Collections.Immutable;

namespace Namotion.Interceptor.Tests.Context;

public class ContextServiceReentrancyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenServiceComparisonReentersLookup_ThenNestedAndCachedResultsContainRegisteredServices(bool reenterFromHashCode)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var firstService = new ReentrantService(reenterFromHashCode);
        var secondService = new ReentrantService(reenterFromHashCode);
        context.AddService(firstService);
        context.AddService(secondService);

        ImmutableArray<IMarkerService> nestedServices = [];
        firstService.Callback = () => nestedServices = context.GetServices<IMarkerService>();

        // Act
        var outerServices = context.GetServices<object>();
        var cachedNestedServices = context.GetServices<IMarkerService>();
        var cachedOuterServices = context.GetServices<object>();

        // Assert
        Assert.Collection(outerServices,
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
        Assert.Collection(nestedServices,
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
        Assert.Collection(cachedNestedServices,
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
        Assert.Collection(cachedOuterServices,
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenServiceComparisonThrowsAfterReentrantLookup_ThenExceptionPropagatesAndLaterLookupsSucceed(bool reenterFromHashCode)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var firstService = new ReentrantService(reenterFromHashCode);
        var secondService = new ReentrantService(reenterFromHashCode);
        context.AddService(firstService);
        context.AddService(secondService);

        var expectedException = new InvalidOperationException("Service comparison failed.");
        ImmutableArray<IMarkerService> nestedServices = [];
        firstService.Callback = () =>
        {
            nestedServices = context.GetServices<IMarkerService>();
            throw expectedException;
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.GetServices<object>());
        Assert.Same(expectedException, exception);
        Assert.Collection(nestedServices,
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
        Assert.Collection(context.GetServices<object>(),
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
        Assert.Collection(context.GetServices<IMarkerService>(),
            service => Assert.Same(firstService, service),
            service => Assert.Same(secondService, service));
    }

    private interface IMarkerService;

    private sealed class ReentrantService(bool reenterFromHashCode) : IMarkerService
    {
        public Action? Callback { get; set; }

        public override int GetHashCode()
        {
            if (reenterFromHashCode)
            {
                InvokeCallback();
            }

            // Both services collide so deduplication also exercises Equals.
            return 42;
        }

        public override bool Equals(object? obj)
        {
            if (!reenterFromHashCode)
            {
                InvokeCallback();
            }

            return ReferenceEquals(this, obj);
        }

        private void InvokeCallback()
        {
            var callback = Callback;
            Callback = null;
            callback?.Invoke();
        }
    }
}
