using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Tests.Ordering;

public class ServiceOrderResolverTests
{
    #region Basic cases

    [Fact]
    public void EmptyList_ReturnsEmptyArray()
    {
        // Arrange
        var services = new List<object>();

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SingleItem_ReturnsSingleItemArray()
    {
        // Arrange
        var service = new ServiceA();
        var services = new List<object> { service };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Single(result);
        Assert.Same(service, result[0]);
    }

    [Fact]
    public void NoAttributes_PreservesRegistrationOrder()
    {
        // Arrange
        var serviceA = new ServiceA();
        var serviceB = new ServiceB();
        var serviceC = new ServiceC();
        var services = new List<object> { serviceA, serviceB, serviceC };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        // Without any ordering attributes, original order should be preserved
        Assert.Same(serviceA, result[0]);
        Assert.Same(serviceB, result[1]);
        Assert.Same(serviceC, result[2]);
    }

    #endregion

    #region RunsBefore/RunsAfter

    [Fact]
    public void RunsBefore_OrdersCorrectly()
    {
        // Arrange: ServiceBeforeA runs before ServiceA
        var serviceA = new ServiceA();
        var serviceBeforeA = new ServiceBeforeA();
        var services = new List<object> { serviceA, serviceBeforeA };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: ServiceBeforeA should come first
        Assert.Equal(2, result.Length);
        Assert.Same(serviceBeforeA, result[0]);
        Assert.Same(serviceA, result[1]);
    }

    [Fact]
    public void RunsAfter_OrdersCorrectly()
    {
        // Arrange: ServiceAfterA runs after ServiceA
        var serviceA = new ServiceA();
        var serviceAfterA = new ServiceAfterA();
        var services = new List<object> { serviceAfterA, serviceA };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: ServiceA should come first
        Assert.Equal(2, result.Length);
        Assert.Same(serviceA, result[0]);
        Assert.Same(serviceAfterA, result[1]);
    }

    [Fact]
    public void MixedConstraints_OrdersCorrectly()
    {
        // Arrange: ServiceBeforeA -> ServiceA -> ServiceAfterA
        var serviceA = new ServiceA();
        var serviceBeforeA = new ServiceBeforeA();
        var serviceAfterA = new ServiceAfterA();
        var services = new List<object> { serviceAfterA, serviceA, serviceBeforeA };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        Assert.Same(serviceBeforeA, result[0]);
        Assert.Same(serviceA, result[1]);
        Assert.Same(serviceAfterA, result[2]);
    }

    [Fact]
    public void TransitiveDependencies_OrdersCorrectly()
    {
        // Arrange: Chain -> ChainMiddle -> ChainEnd (via RunsBefore)
        var chainStart = new ChainStart();
        var chainMiddle = new ChainMiddle();
        var chainEnd = new ChainEnd();
        var services = new List<object> { chainEnd, chainStart, chainMiddle };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        Assert.Same(chainStart, result[0]);
        Assert.Same(chainMiddle, result[1]);
        Assert.Same(chainEnd, result[2]);
    }

    [Fact]
    public void MissingDependencyType_SilentlyIgnored()
    {
        // Arrange: ServiceBeforeA references ServiceA, but ServiceA is not in the list
        var serviceBeforeA = new ServiceBeforeA();
        var services = new List<object> { serviceBeforeA };

        // Act - should not throw
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Single(result);
        Assert.Same(serviceBeforeA, result[0]);
    }

    #endregion

    #region RunsFirst/RunsLast

    [Fact]
    public void RunsFirst_RunsBeforeMiddleAndLast()
    {
        // Arrange
        var first = new FirstService();
        var middle = new ServiceA();
        var last = new LastService();
        var services = new List<object> { middle, last, first };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        Assert.Same(first, result[0]);
        Assert.Same(middle, result[1]);
        Assert.Same(last, result[2]);
    }

    [Fact]
    public void RunsLast_RunsAfterFirstAndMiddle()
    {
        // Arrange
        var first = new FirstService();
        var middle = new ServiceA();
        var last = new LastService();
        var services = new List<object> { last, first, middle };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        Assert.Same(first, result[0]);
        Assert.Same(middle, result[1]);
        Assert.Same(last, result[2]);
    }

    [Fact]
    public void MultipleRunsFirst_UsesRunsBeforeWithinGroup()
    {
        // Arrange: Two FirstServices, one runs before the other
        var first1 = new FirstService();
        var first2 = new FirstServiceBeforeFirst1();
        var middle = new ServiceA();
        var services = new List<object> { middle, first1, first2 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: first2 -> first1 -> middle
        Assert.Equal(3, result.Length);
        Assert.Same(first2, result[0]);
        Assert.Same(first1, result[1]);
        Assert.Same(middle, result[2]);
    }

    [Fact]
    public void MultipleRunsLast_UsesRunsAfterWithinGroup()
    {
        // Arrange: Two LastServices, one runs after the other
        var middle = new ServiceA();
        var last1 = new LastService();
        var last2 = new LastServiceAfterLast1();
        var services = new List<object> { last2, middle, last1 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: middle -> last1 -> last2
        Assert.Equal(3, result.Length);
        Assert.Same(middle, result[0]);
        Assert.Same(last1, result[1]);
        Assert.Same(last2, result[2]);
    }

    #endregion

    #region Error cases

    [Fact]
    public void CircularDependency_ThrowsWithTypeNames()
    {
        // Arrange: Circular1 -> Circular2 -> Circular1
        var circular1 = new Circular1();
        var circular2 = new Circular2();
        var services = new List<object> { circular1, circular2 };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceOrderResolver.OrderByDependencies(services));
        Assert.Contains("Circular", ex.Message);
        Assert.Contains("Circular1", ex.Message);
        Assert.Contains("Circular2", ex.Message);
    }

    [Fact]
    public void RunsFirstAndRunsLast_ThrowsException()
    {
        // Arrange
        var invalid = new FirstAndLastService();
        var services = new List<object> { invalid };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceOrderResolver.OrderByDependencies(services));
        Assert.Contains("[RunsFirst]", ex.Message);
        Assert.Contains("[RunsLast]", ex.Message);
    }

    [Fact]
    public void RunsFirst_WithRunsAfterOnMiddle_ThrowsException()
    {
        // Arrange: FirstService with RunsAfter pointing to middle group
        var invalid = new FirstServiceWithRunsAfterMiddle();
        var middle = new ServiceA();
        var services = new List<object> { invalid, middle };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceOrderResolver.OrderByDependencies(services));
        Assert.Contains("[RunsFirst]", ex.Message);
        Assert.Contains("[RunsAfter", ex.Message);
    }

    [Fact]
    public void RunsLast_WithRunsBeforeOnMiddle_ThrowsException()
    {
        // Arrange: LastService with RunsBefore pointing to middle group
        var invalid = new LastServiceWithRunsBeforeMiddle();
        var middle = new ServiceA();
        var services = new List<object> { invalid, middle };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceOrderResolver.OrderByDependencies(services));
        Assert.Contains("[RunsLast]", ex.Message);
        Assert.Contains("[RunsBefore", ex.Message);
    }

    #endregion

    #region Test services

    // Basic services without attributes
    private class ServiceA { }
    private class ServiceB { }
    private class ServiceC { }

    // RunsBefore/RunsAfter services
    [RunsBefore(typeof(ServiceA))]
    private class ServiceBeforeA { }

    [RunsAfter(typeof(ServiceA))]
    private class ServiceAfterA { }

    // Transitive chain: ChainStart -> ChainMiddle -> ChainEnd
    [RunsBefore(typeof(ChainMiddle))]
    private class ChainStart { }

    [RunsBefore(typeof(ChainEnd))]
    private class ChainMiddle { }

    private class ChainEnd { }

    // RunsFirst/RunsLast services
    [RunsFirst]
    private class FirstService { }

    [RunsLast]
    private class LastService { }

    [RunsFirst]
    [RunsBefore(typeof(FirstService))]
    private class FirstServiceBeforeFirst1 { }

    [RunsLast]
    [RunsAfter(typeof(LastService))]
    private class LastServiceAfterLast1 { }

    // Error case services
    [RunsBefore(typeof(Circular2))]
    private class Circular1 { }

    [RunsBefore(typeof(Circular1))]
    private class Circular2 { }

    [RunsFirst]
    [RunsLast]
    private class FirstAndLastService { }

    [RunsFirst]
    [RunsAfter(typeof(ServiceA))]
    private class FirstServiceWithRunsAfterMiddle { }

    [RunsLast]
    [RunsBefore(typeof(ServiceA))]
    private class LastServiceWithRunsBeforeMiddle { }

    #endregion
}
