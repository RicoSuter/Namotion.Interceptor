using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyReattachTests
{
    [Fact]
    public void WhenDerivedPropertyIsReattachedBeforeItsSource_ThenDependencyIsRestored()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions()
            .WithLifecycle();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var fullNameProperty = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));

        person.DetachSubjectProperty(fullNameProperty);
        person.DetachSubjectProperty(firstNameProperty);
        person.DetachSubjectProperty(lastNameProperty);

        // Act
        person.AttachSubjectProperty(fullNameProperty);
        person.AttachSubjectProperty(firstNameProperty);
        person.AttachSubjectProperty(lastNameProperty);

        changes.Clear();
        person.FirstName = "Jane";

        // Assert
        Assert.Contains(firstNameProperty, fullNameProperty.GetRequiredProperties().ToArray());
        Assert.Contains(lastNameProperty, fullNameProperty.GetRequiredProperties().ToArray());
        Assert.Contains(fullNameProperty, firstNameProperty.GetUsedByProperties().Items.ToArray());
        Assert.Contains(changes, change =>
            change.Property == fullNameProperty &&
            change.GetNewValue<string?>() == "Jane Doe");
    }

    [Fact]
    public void WhenSubjectIsDetached_ThenSourceTrackingDataIsRemovedAndDerivedDataIsKeptDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(context);
        var child = new Person { FirstName = "John", LastName = "Doe" };
        parent.Mother = child;

        var fullNameProperty = new PropertyReference(child, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(child, nameof(Person.FirstName));
        Assert.NotNull(firstNameProperty.TryGetDerivedPropertyData());

        // Act
        parent.Mother = null;

        // Assert
        Assert.Null(firstNameProperty.TryGetDerivedPropertyData());
        Assert.False(fullNameProperty.TryGetDerivedPropertyData()!.IsAttached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectWithDerivedPropertyIsRemovedWhileItsSourceStaysAttached_ThenSubjectCanBeGarbageCollected(bool moveBeforeRemoval)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var garage = new Garage(context);

        // Act
        var weakCar = AddCarSharingATireAndRemoveIt(garage, moveBeforeRemoval);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        var sharedTire = garage.SpareTires[0];
        Assert.False(weakCar.IsAlive);
        Assert.Empty(new PropertyReference(sharedTire, nameof(Tire.Pressure)).GetUsedByProperties().Items.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddCarSharingATireAndRemoveIt(Garage garage, bool moveBeforeRemoval)
    {
        // The shared tire joins the garage first, so its inherited context is the garage's,
        // and the derived property is the only link from the tire back to the car.
        var car = new Car();
        garage.SpareTires = [car.Tires[0]];
        garage.Cars = [car];

        if (moveBeforeRemoval)
        {
            garage.Cars = [];
            garage.CarsByName = new Dictionary<string, Car> { ["moved"] = car };
            garage.CarsByName = new Dictionary<string, Car>();
        }
        else
        {
            garage.Cars = [];
        }

        return new WeakReference(car);
    }
}
