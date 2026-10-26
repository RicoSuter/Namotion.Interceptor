using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class FallbackContextCleanupTests
{
    private sealed class RootMarker;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectConstructedWithContextLeavesOtherParent_ThenItIsCollected(bool fullTracking)
    {
        // Arrange
        var context = CreateContext(fullTracking);
        var root = new Person(context);
        var parent = new Person();
        root.Children = [parent];

        // Act
        var child = PlaceAndRemoveChildConstructedWithContext(context, parent);
        CollectGarbage();

        // Assert
        Assert.False(child.IsAlive);
        GC.KeepAlive(root);
    }

    [Fact]
    public void WhenSubjectConstructedWithContextLeavesOtherParent_ThenItNoLongerResolvesThroughContext()
    {
        // Arrange
        var context = CreateContext(fullTracking: false);
        context.AddService(new RootMarker());
        var root = new Person(context);
        var parent = new Person();
        root.Children = [parent];

        var child = new Person(context);
        parent.Children = [child];

        // Act
        parent.Children = [];

        // Assert
        Assert.Empty(((IInterceptorSubject)child).Context.GetServices<RootMarker>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectLeavesTwoParentsInAttachOrder_ThenItIsCollected(bool fullTracking)
    {
        // Arrange
        var context = CreateContext(fullTracking);
        var root = new Person(context);
        var firstParent = new Person();
        var secondParent = new Person();
        root.Children = [firstParent, secondParent];

        // Act
        var child = AttachToBothAndRemoveInAttachOrder(firstParent, secondParent);
        CollectGarbage();

        // Assert
        Assert.False(child.IsAlive);
        GC.KeepAlive(root);
    }

    [Fact]
    public void WhenParentIsRemovedWhileItsChildStaysAttachedElsewhere_ThenParentIsCollected()
    {
        // Arrange
        var context = CreateContext(fullTracking: true);
        var garage = new Garage(context);

        // Act
        var car = AttachCarAndKeepItsTireAsSpare(garage);
        CollectGarbage();

        // Assert
        Assert.False(car.IsAlive);
        GC.KeepAlive(garage);
    }

    [Fact]
    public void WhenSubjectLeavesOneOfTwoParents_ThenItKeepsResolvingThroughContext()
    {
        // Arrange
        var context = CreateContext(fullTracking: false);
        context.AddService(new RootMarker());
        var root = new Person(context);
        var firstParent = new Person();
        var secondParent = new Person();
        root.Children = [firstParent, secondParent];

        var child = new Person();
        firstParent.Children = [child];
        secondParent.Children = [child];

        // Act
        firstParent.Children = [];

        // Assert
        Assert.Single(((IInterceptorSubject)child).Context.GetServices<RootMarker>());
    }

    [Fact]
    public void WhenDetachedSubjectReentersGraph_ThenItResolvesThroughNewParent()
    {
        // Arrange
        var context = CreateContext(fullTracking: false);
        context.AddService(new RootMarker());
        var root = new Person(context);
        var firstParent = new Person();
        var secondParent = new Person();
        root.Children = [firstParent, secondParent];

        var child = new Person(context);
        firstParent.Children = [child];
        firstParent.Children = [];

        // Act
        secondParent.Children = [child];

        // Assert
        Assert.Single(((IInterceptorSubject)child).Context.GetServices<RootMarker>());
        Assert.True(((IInterceptorSubject)child).Context.RemoveFallbackContext(((IInterceptorSubject)secondParent).Context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenRootConstructedWithContextIsDropped_ThenContextKeepsItAliveUntilRemoved(bool lifecycle)
    {
        // Arrange
        var context = lifecycle ? CreateContext(fullTracking: true) : InterceptorSubjectContext.Create();

        // Act
        var root = ConstructRoot(context);
        CollectGarbage();
        var aliveBeforeRemoval = root.IsAlive;
        RemoveContext(root, context);
        CollectGarbage();

        // Assert
        Assert.True(aliveBeforeRemoval);
        Assert.False(root.IsAlive);
    }

    private static IInterceptorSubjectContext CreateContext(bool fullTracking)
    {
        var context = InterceptorSubjectContext.Create();
        return fullTracking ? context.WithFullPropertyTracking() : context.WithContextInheritance();
    }

    // The helpers below are kept out of the test methods, so that no local of a test keeps the
    // subject under observation alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PlaceAndRemoveChildConstructedWithContext(IInterceptorSubjectContext context, Person parent)
    {
        var child = new Person(context);
        parent.Children = [child];
        parent.Children = [];
        return new WeakReference(child);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachToBothAndRemoveInAttachOrder(Person firstParent, Person secondParent)
    {
        var child = new Person();
        firstParent.Children = [child];
        secondParent.Children = [child];
        firstParent.Children = [];
        secondParent.Children = [];
        return new WeakReference(child);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachCarAndKeepItsTireAsSpare(Garage garage)
    {
        var car = new Car();
        garage.Cars = [car];
        garage.SpareTires = [car.Tires[0]];
        garage.Cars = [];
        return new WeakReference(car);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ConstructRoot(IInterceptorSubjectContext context)
    {
        return new WeakReference(new Person(context));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveContext(WeakReference root, IInterceptorSubjectContext context)
    {
        ((IInterceptorSubject)root.Target!).Context.RemoveFallbackContext(context);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
