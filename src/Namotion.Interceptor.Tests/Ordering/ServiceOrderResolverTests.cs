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
        var services = Array.Empty<object>();

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
        var services = new object[] { service };

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
        var services = new object[] { serviceA, serviceB, serviceC };

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
        var services = new object[] { serviceA, serviceBeforeA };

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
        var services = new object[] { serviceAfterA, serviceA };

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
        var services = new object[] { serviceAfterA, serviceA, serviceBeforeA };

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
        var services = new object[] { chainEnd, chainStart, chainMiddle };

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
        var services = new object[] { serviceBeforeA };

        // Act - should not throw
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Single(result);
        Assert.Same(serviceBeforeA, result[0]);
    }

    [Fact]
    public void ServiceWithDependency_PreservesOrderOfUnrelatedServices()
    {
        // Arrange: Register A, B, C, D, E where A runs before D
        // Registration order: A, B, C, D, E
        // Expected: A, B, C, D, E (D must come after A, unrelated services keep relative order)
        var serviceA = new ServiceARunsBeforeD();
        var serviceB = new ServiceB();
        var serviceC = new ServiceC();
        var serviceD = new ServiceD();
        var serviceE = new ServiceE();
        var services = new object[] { serviceA, serviceB, serviceC, serviceD, serviceE };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: A should be before D, and unrelated services should maintain registration order
        Assert.Equal(5, result.Length);

        // A must be before D (due to constraint)
        var indexA = Array.IndexOf(result, serviceA);
        var indexD = Array.IndexOf(result, serviceD);
        Assert.True(indexA < indexD, "A should come before D");

        // B, C, E should maintain their relative registration order
        var indexB = Array.IndexOf(result, serviceB);
        var indexC = Array.IndexOf(result, serviceC);
        var indexE = Array.IndexOf(result, serviceE);
        Assert.True(indexB < indexC, "B should come before C (registration order)");
        Assert.True(indexC < indexE, "C should come before E (registration order)");

        // D should come before E (registration order)
        Assert.True(indexD < indexE, "D should come before E (registration order)");
    }

    [Fact]
    public void ComplexDependencyChain_PreservesUnrelatedOrder()
    {
        // Arrange: X -> Y -> Z (chain), plus unrelated A, B, C interspersed
        // Registration order: A, X, B, Y, C, Z
        // Expected: A, X, B, Y, C, Z (chain respected, unrelated services in place)
        var serviceA = new ServiceA();
        var serviceX = new ChainStart();
        var serviceB = new ServiceB();
        var serviceY = new ChainMiddle();
        var serviceC = new ServiceC();
        var serviceZ = new ChainEnd();
        var services = new object[] { serviceA, serviceX, serviceB, serviceY, serviceC, serviceZ };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(6, result.Length);

        // Chain must be maintained: X -> Y -> Z
        var indexX = Array.IndexOf(result, serviceX);
        var indexY = Array.IndexOf(result, serviceY);
        var indexZ = Array.IndexOf(result, serviceZ);
        Assert.True(indexX < indexY, "X should come before Y");
        Assert.True(indexY < indexZ, "Y should come before Z");

        // Unrelated services should maintain their relative order
        var indexA = Array.IndexOf(result, serviceA);
        var indexB = Array.IndexOf(result, serviceB);
        var indexC = Array.IndexOf(result, serviceC);
        Assert.True(indexA < indexB, "A should come before B (registration order)");
        Assert.True(indexB < indexC, "B should come before C (registration order)");
    }

    [Fact]
    public void MultipleRunsBeforeAttributes_OrdersCorrectly()
    {
        // Arrange: ServiceBeforeAAndB has two [RunsBefore] attributes
        var serviceA = new ServiceA();
        var serviceB = new ServiceB();
        var serviceBeforeAAndB = new ServiceBeforeAAndB();
        var services = new object[] { serviceA, serviceB, serviceBeforeAAndB };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: ServiceBeforeAAndB should come first
        Assert.Equal(3, result.Length);
        Assert.Same(serviceBeforeAAndB, result[0]);
    }

    [Fact]
    public void CombinedRunsBeforeAndRunsAfter_OrdersCorrectly()
    {
        // Arrange: ServiceBetweenAAndC runs after A and before C
        var serviceA = new ServiceA();
        var serviceC = new ServiceC();
        var serviceBetween = new ServiceBetweenAAndC();
        var services = new object[] { serviceC, serviceBetween, serviceA };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: A -> Between -> C
        Assert.Equal(3, result.Length);
        Assert.Same(serviceA, result[0]);
        Assert.Same(serviceBetween, result[1]);
        Assert.Same(serviceC, result[2]);
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
        var services = new object[] { middle, last, first };

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
        var services = new object[] { last, first, middle };

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
        var services = new object[] { middle, first1, first2 };

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
        var services = new object[] { last2, middle, last1 };

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
        var services = new object[] { circular1, circular2 };

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
        var services = new object[] { invalid };

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
        var services = new object[] { invalid, middle };

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
        var services = new object[] { invalid, middle };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceOrderResolver.OrderByDependencies(services));
        Assert.Contains("[RunsLast]", ex.Message);
        Assert.Contains("[RunsBefore", ex.Message);
    }

    #endregion

    #region Multi-instance (duplicated types)

    [Fact]
    public void RunsBefore_WithDuplicatedTarget_OrdersBeforeAllInstances()
    {
        // Arrange: [D0, C, D1] where C runs before D; last-index binding leaves D0 before C
        var duplicated0 = new DuplicatedService();
        var constrainer = new ServiceBeforeDuplicated();
        var duplicated1 = new DuplicatedService();
        var services = new object[] { duplicated0, constrainer, duplicated1 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: constrainer first, duplicates keep registration order
        Assert.Equal(3, result.Length);
        Assert.Same(constrainer, result[0]);
        Assert.Same(duplicated0, result[1]);
        Assert.Same(duplicated1, result[2]);
    }

    [Fact]
    public void DuplicatedType_WithinRunsFirstGroup_OrdersAgainstAllInstances()
    {
        // Arrange: two RunsFirst duplicates around a RunsFirst constrainer, plus a middle service
        var duplicatedFirst0 = new DuplicatedFirstService();
        var middle = new ServiceA();
        var constrainer = new FirstServiceBeforeDuplicatedFirst();
        var duplicatedFirst1 = new DuplicatedFirstService();
        var services = new object[] { duplicatedFirst0, middle, constrainer, duplicatedFirst1 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: within the first group the constrainer precedes both duplicates; middle service last
        Assert.Equal(4, result.Length);
        Assert.Same(constrainer, result[0]);
        Assert.Same(duplicatedFirst0, result[1]);
        Assert.Same(duplicatedFirst1, result[2]);
        Assert.Same(middle, result[3]);
    }

    [Fact]
    public void RunsAfter_WithDuplicatedTarget_OrdersAfterAllInstances()
    {
        // Arrange: constrained service registered first, two duplicates after it
        var constrained = new ServiceAfterDuplicated();
        var duplicated0 = new DuplicatedService();
        var duplicated1 = new DuplicatedService();
        var services = new object[] { constrained, duplicated0, duplicated1 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: both duplicates precede the constrained service
        Assert.Equal(3, result.Length);
        Assert.Same(duplicated0, result[0]);
        Assert.Same(duplicated1, result[1]);
        Assert.Same(constrained, result[2]);
    }

    [Fact]
    public void DuplicatedType_WithoutConstraints_PreservesRegistrationOrder()
    {
        // Arrange: duplicates that no constraint references
        var duplicated0 = new DuplicatedService();
        var serviceB = new ServiceB();
        var duplicated1 = new DuplicatedService();
        var services = new object[] { duplicated0, serviceB, duplicated1 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        Assert.Same(duplicated0, result[0]);
        Assert.Same(serviceB, result[1]);
        Assert.Same(duplicated1, result[2]);
    }

    [Fact]
    public void DuplicatedType_InTypeLevelCycle_ThrowsCircularDependency()
    {
        // Arrange: mutual RunsBefore between two types, one of them duplicated
        var services = new object[] { new Circular1(), new Circular2(), new Circular1() };

        // Act & Assert: throw outcome is invariant under the fix; only the set of
        // listed type names may differ, so assert the prefix, never the exact message
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServiceOrderResolver.OrderByDependencies(services));
        Assert.Contains("Circular dependency detected", ex.Message);
    }

    [Fact]
    public void DuplicatedInstances_OfConstrainedType_PreserveRegistrationOrder()
    {
        // Arrange: three duplicates, all constrained by the same RunsAfter service
        var duplicated0 = new DuplicatedService();
        var constrained = new ServiceAfterDuplicated();
        var duplicated1 = new DuplicatedService();
        var duplicated2 = new DuplicatedService();
        var services = new object[] { duplicated0, constrained, duplicated1, duplicated2 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert: duplicates emit in registration order relative to each other
        Assert.Equal(4, result.Length);
        Assert.Same(duplicated0, result[0]);
        Assert.Same(duplicated1, result[1]);
        Assert.Same(duplicated2, result[2]);
        Assert.Same(constrained, result[3]);
    }

    [Fact]
    public void ParallelEdges_WithDuplicatedTarget_SortCleanly()
    {
        // Arrange: U declares RunsBefore(T) and T declares RunsAfter(U), so every
        // instance pair gets the same edge twice; in-degree accounting must stay symmetric
        var target0 = new DuplicatedTargetService();
        var source = new ParallelEdgeSource();
        var target1 = new DuplicatedTargetService();
        var services = new object[] { target0, source, target1 };

        // Act
        var result = ServiceOrderResolver.OrderByDependencies(services);

        // Assert
        Assert.Equal(3, result.Length);
        Assert.Same(source, result[0]);
        Assert.Same(target0, result[1]);
        Assert.Same(target1, result[2]);
    }

    #endregion

    #region Test services

    // Basic services without attributes
    private class ServiceA { }
    private class ServiceB { }
    private class ServiceC { }
    private class ServiceD { }
    private class ServiceE { }

    // Service that runs before D (for order preservation tests)
    [RunsBefore(typeof(ServiceD))]
    private class ServiceARunsBeforeD { }

    // RunsBefore/RunsAfter services
    [RunsBefore(typeof(ServiceA))]
    private class ServiceBeforeA { }

    [RunsAfter(typeof(ServiceA))]
    private class ServiceAfterA { }

    [RunsBefore(typeof(ServiceA))]
    [RunsBefore(typeof(ServiceB))]
    private class ServiceBeforeAAndB { }

    [RunsAfter(typeof(ServiceA))]
    [RunsBefore(typeof(ServiceC))]
    private class ServiceBetweenAAndC { }

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

    // Multi-instance services (duplicate-type aggregation, issue #380)
    private class DuplicatedService { }

    [RunsBefore(typeof(DuplicatedService))]
    private class ServiceBeforeDuplicated { }

    [RunsFirst]
    private class DuplicatedFirstService { }

    [RunsFirst]
    [RunsBefore(typeof(DuplicatedFirstService))]
    private class FirstServiceBeforeDuplicatedFirst { }

    [RunsAfter(typeof(DuplicatedService))]
    private class ServiceAfterDuplicated { }

    // Parallel-edge pair: both sides declare the same relationship
    [RunsBefore(typeof(DuplicatedTargetService))]
    private class ParallelEdgeSource { }

    [RunsAfter(typeof(ParallelEdgeSource))]
    private class DuplicatedTargetService { }

    #endregion
}
