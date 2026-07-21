using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

public class InterceptorSubjectContextTests
{
    [Fact]
    public void WhenAddingSingleService_ThenItCanBeRetrieved()
    {
        // Arrange
        var context = new InterceptorSubjectContext();

        // Act
        context.AddService(1);

        // Assert
        Assert.Equal(1, context.GetService<int>());
    }
    
    [Fact]
    public void WhenAddingTwoServices_ThenListCanBeRetrieved()
    {
        // Arrange
        var context = new InterceptorSubjectContext();

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
        var context1 = new InterceptorSubjectContext();
        var context2 = new InterceptorSubjectContext();
        
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
        var parent = new InterceptorSubjectContext();
        var fallback1 = new InterceptorSubjectContext();
        var fallback2 = new InterceptorSubjectContext();

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

    private interface IOrderedTestService { }

    private class DuplicateOrderedService : IOrderedTestService { }

    [RunsBefore(typeof(DuplicateOrderedService))]
    private class ConstrainerOrderedService : IOrderedTestService { }
}