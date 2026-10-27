using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A subject the graph releases is no longer referenced by the context or by the graph it left, so
/// nothing but the caller keeps it alive.
/// </summary>
public class ReleasedSubjectCollectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectConstructedWithContextIsPlacedAndRemoved_ThenItIsCollected(bool fullTracking)
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenParentIsRemovedWhileItsChildStaysUnderAnotherParent_ThenTheParentIsCollected(bool fullTracking)
    {
        // Arrange
        var context = CreateContext(fullTracking);
        var garage = new Garage(context);

        // Act
        var car = AttachCarAndKeepItsTireAsSpare(garage);
        CollectGarbage();

        // Assert
        Assert.False(car.IsAlive);
        Assert.Single(garage.SpareTires);
    }

    private static IInterceptorSubjectContext CreateContext(bool fullTracking)
    {
        var context = InterceptorSubjectContext.Create();
        return fullTracking ? context.WithFullPropertyTracking() : context.WithLifecycle();
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

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
