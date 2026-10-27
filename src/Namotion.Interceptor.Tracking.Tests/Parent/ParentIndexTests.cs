using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// A structural write that keeps a subject in the property fires neither detach nor attach for it,
/// so its parent entry has to follow the new key or position on its own and still go away when the
/// subject finally leaves the property.
/// </summary>
public class ParentIndexTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();
    }

    private static SubjectParent Parent(IInterceptorSubject subject, string propertyName, object? index)
    {
        return new SubjectParent(new PropertyReference(subject, propertyName), index);
    }

    [Fact]
    public void WhenMovedImmutableArrayItemIsRemoved_ThenRemovedParentIsNotTraversed()
    {
        // Arrange
        var context = CreateContext();
        var spare = new Tire();
        var tire = new Tire();
        _ = new Car(context) { Tires = [tire] };
        var garage = new Garage(context) { SpareTires = [spare, tire] };

        garage.SpareTires = [tire, spare];

        // Act
        garage.SpareTires = [spare];

        // Assert
        Assert.Null(tire.TryGetFirstParent<Garage>());
    }

    [Fact]
    public void WhenSubjectLeavesOneOfTwoPropertiesOfItsParent_ThenTheOtherParentStays()
    {
        // Arrange
        var car = new Car();
        var garage = new Garage(CreateContext().WithRegistry())
        {
            Cars = [car],
            CarsByName = new Dictionary<string, Car> { ["Motor"] = car }
        };

        // Act
        garage.Cars = [];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Motor"), Assert.Single(car.GetParents()));
        var registeredParent = Assert.Single(car.TryGetRegisteredSubject()!.Parents);
        Assert.Equal(nameof(Garage.CarsByName), registeredParent.Property.Name);
        Assert.Equal("Motor", registeredParent.Index);
    }

    [Fact]
    public void WhenStructuralWriteKeepsSubjectHeldTwice_ThenParentsSnapshotIsReused()
    {
        // Arrange
        var car = new Car();
        var other = new Car();
        var garage = new Garage(CreateContext()) { Cars = [car, other, car] };
        var parentsOfCar = car.GetParents();

        // Act
        garage.Cars = [car, other, car];

        // Assert: equal means the same underlying array, so the unchanged index cost no new snapshot.
        Assert.True(parentsOfCar == car.GetParents());
    }

    [Fact]
    public void WhenStructuralWriteKeepsEveryIndex_ThenParentsSnapshotIsReused()
    {
        // Arrange
        var car = new Car();
        var keyed = new Car();
        var garage = new Garage(CreateContext())
        {
            Cars = [car],
            CarsByName = new Dictionary<string, Car> { ["Motor"] = keyed }
        };

        var parentsOfCar = car.GetParents();
        var parentsOfKeyed = keyed.GetParents();

        // Act
        garage.Cars = [car, new Car()];
        garage.CarsByName = new Dictionary<string, Car> { ["Motor"] = keyed, ["Added"] = new Car() };

        // Assert: equal means the same underlying array, so an unchanged index cost no new snapshot.
        Assert.True(parentsOfCar == car.GetParents());
        Assert.True(parentsOfKeyed == keyed.GetParents());
    }
}
