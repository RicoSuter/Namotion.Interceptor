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
}