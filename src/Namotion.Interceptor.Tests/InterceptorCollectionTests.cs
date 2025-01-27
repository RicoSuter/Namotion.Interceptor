namespace Namotion.Interceptor.Tests;

public class InterceptorCollectionTests
{
    [Fact]
    public void WhenAddingSingleService_ThenItCanBeRetrieved()
    {
        // Arrange
        var collection = new InterceptorCollection();

        // Act
        collection.AddService(1);

        // Assert
        Assert.Equal(1, collection.GetService<int>());
    }
    
    [Fact]
    public void WhenAddingTwoServices_ThenListCanBeRetrieved()
    {
        // Arrange
        var collection = new InterceptorCollection();

        // Act
        collection.AddService(1);
        collection.AddService(2);

        // Assert
        var services = collection
            .GetServices<int>()
            .ToArray();
        
        Assert.Contains(1, services);
        Assert.Contains(2, services);
        Assert.Equal(2, services.Length);
        
        Assert.Throws<InvalidOperationException>(() => collection.GetService<int>());
    }
}