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
    public void WhenFallbackContextIsRemoved_ThenTheOthersAreListedInOrderAndEarlierSnapshotIsUnchanged()
    {
        // Arrange
        var first = InterceptorSubjectContext.Create();
        var second = InterceptorSubjectContext.Create();
        var third = InterceptorSubjectContext.Create();
        var context = InterceptorSubjectContext.Create();
        context.AddFallbackContext(first);
        context.AddFallbackContext(second);
        context.AddFallbackContext(third);
        var snapshot = context.FallbackContexts;

        // Act
        context.RemoveFallbackContext(second);

        // Assert
        Assert.Equal(new IInterceptorSubjectContext[] { first, third }, context.FallbackContexts);
        Assert.Equal(new IInterceptorSubjectContext[] { first, second, third }, snapshot);
    }

    private interface IOrderedTestService { }

    private class DuplicateOrderedService : IOrderedTestService { }

    [RunsBefore(typeof(DuplicateOrderedService))]
    private class ConstrainerOrderedService : IOrderedTestService { }
}
