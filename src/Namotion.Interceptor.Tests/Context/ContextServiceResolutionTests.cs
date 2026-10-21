using System.Runtime.InteropServices;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests.Context;

public class ContextServiceResolutionTests
{
    [Fact]
    public void WhenAddingSingleService_ThenItCanBeRetrieved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        context.AddService(1);

        // Assert
        Assert.Equal(1, context.GetService<int>());
    }
    
    [Fact]
    public void WhenAddingTwoServices_ThenListCanBeRetrieved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        context.AddService(1);
        context.AddService(2);

        // Assert
        var services = context
            .GetServices<int>()
            .ToArray();
        
        Assert.Contains(1, services);
        Assert.Contains(2, services);
        Assert.Equal(2, services.Length);
        
        Assert.Throws<InvalidOperationException>(() => context.GetService<int>());
    }
    
    [Fact]
    public void WhenCollectionHasSubCollection_ThenServicesAreInherited()
    {
        // Arrange
        var context1 = InterceptorSubjectContext.Create();
        var context2 = InterceptorSubjectContext.Create();
        
        context2.AddFallbackContext(context1);

        // Act
        context1.AddService(1);
        context1.AddService(2);
        context2.AddService(3);

        // Assert
        Assert.Equal(2, context1.GetServices<int>().Count());
        Assert.Equal(3, context2.GetServices<int>().Count());
    }

    [Fact]
    public void WhenFallbackContextsRegisterSameServiceType_ThenOrderingAttributeBindsAgainstAllInstances()
    {
        // Arrange: the duplicate in the first fallback enumerates before the constrainer
        // in the second, so last-index binding leaves it unordered (issue #380); relies on
        // fallback enumeration following HashSet insertion order (true in practice, not contractual)
        var parent = InterceptorSubjectContext.Create();
        var fallback1 = InterceptorSubjectContext.Create();
        var fallback2 = InterceptorSubjectContext.Create();

        var duplicate0 = new DuplicateOrderedService();
        var constrainer = new ConstrainerOrderedService();
        var duplicate1 = new DuplicateOrderedService();

        fallback1.AddService(duplicate0);
        fallback2.AddService(constrainer);
        fallback2.AddService(duplicate1);

        parent.AddFallbackContext(fallback1);
        parent.AddFallbackContext(fallback2);

        // Act
        var services = parent.GetServices<IOrderedTestService>();

        // Assert: the constrainer precedes both duplicate instances
        Assert.Equal(3, services.Length);
        Assert.Same(constrainer, services[0]);
        Assert.Same(duplicate0, services[1]);
        Assert.Same(duplicate1, services[2]);
    }

    [Fact]
    public void WhenSubjectContextsFallBackToSameContext_ThenGetServicesReturnsSameArrayInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new MarkerService());
        var first = new Car(context);
        var second = new Car(context);

        // Act
        var firstServices = ((IInterceptorSubject)first).Context.GetServices<MarkerService>();
        var secondServices = ((IInterceptorSubject)second).Context.GetServices<MarkerService>();

        // Assert
        Assert.Same(ImmutableCollectionsMarshal.AsArray(firstServices), ImmutableCollectionsMarshal.AsArray(secondServices));
        Assert.Same(ImmutableCollectionsMarshal.AsArray(context.GetServices<MarkerService>()), ImmutableCollectionsMarshal.AsArray(firstServices));
    }

    [Fact]
    public void WhenSubjectContextsFallBackToDifferentContexts_ThenGetServicesReturnsDifferentArrayInstances()
    {
        // Arrange
        var service = new MarkerService();
        var firstContext = InterceptorSubjectContext.Create();
        var secondContext = InterceptorSubjectContext.Create();
        firstContext.AddService(service);
        secondContext.AddService(service);
        var first = new Car(firstContext);
        var second = new Car(secondContext);

        // Act
        var firstServices = ((IInterceptorSubject)first).Context.GetServices<MarkerService>();
        var secondServices = ((IInterceptorSubject)second).Context.GetServices<MarkerService>();

        // Assert
        Assert.Same(service, Assert.Single(firstServices));
        Assert.Same(service, Assert.Single(secondServices));
        Assert.NotSame(ImmutableCollectionsMarshal.AsArray(firstServices), ImmutableCollectionsMarshal.AsArray(secondServices));
    }

    [Fact]
    public void WhenServiceIsAddedToFallbackContext_ThenGetServicesReturnsNewArrayInstance()
    {
        // Arrange
        var service = new MarkerService();
        var context = InterceptorSubjectContext.Create();
        context.AddService(service);
        var subject = new Car(context);
        var servicesBefore = ((IInterceptorSubject)subject).Context.GetServices<MarkerService>();

        // Act
        context.AddService(new object());
        var servicesAfter = ((IInterceptorSubject)subject).Context.GetServices<MarkerService>();

        // Assert
        Assert.Same(service, Assert.Single(servicesBefore));
        Assert.Same(service, Assert.Single(servicesAfter));
        Assert.NotSame(ImmutableCollectionsMarshal.AsArray(servicesBefore), ImmutableCollectionsMarshal.AsArray(servicesAfter));
    }

    [Fact]
    public void WhenFallbackOfContextWithOwnServicesGainsService_ThenGetServicesReturnsNewArrayInstance()
    {
        // Arrange
        var service = new MarkerService();
        var fallback = InterceptorSubjectContext.Create();
        fallback.AddService(service);
        var context = InterceptorSubjectContext.Create();
        context.AddService(new object());
        context.AddFallbackContext(fallback);
        var servicesBefore = context.GetServices<MarkerService>();

        // Act
        fallback.AddService(new object());
        var servicesAfter = context.GetServices<MarkerService>();

        // Assert
        Assert.Same(service, Assert.Single(servicesBefore));
        Assert.Same(service, Assert.Single(servicesAfter));
        Assert.NotSame(ImmutableCollectionsMarshal.AsArray(servicesBefore), ImmutableCollectionsMarshal.AsArray(servicesAfter));
    }

    private sealed class MarkerService;

    private interface IOrderedTestService { }

    private class DuplicateOrderedService : IOrderedTestService { }

    [RunsBefore(typeof(DuplicateOrderedService))]
    private class ConstrainerOrderedService : IOrderedTestService { }
}
