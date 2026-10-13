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
    public void WhenDictionaryEntryIsReKeyed_ThenParentIndexIsTheNewKey()
    {
        // Arrange
        var car = new Car();
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["Motor"] = car }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["Engine"] = car };

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Engine"), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenReKeyedDictionaryEntryIsRemoved_ThenParentIsRemoved()
    {
        // Arrange
        var context = CreateContext();
        var car = new Car();
        var holder = new Garage(context) { Cars = [car] };
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["Motor"] = car }
        };

        garage.CarsByName = new Dictionary<string, Car> { ["Engine"] = car };

        // Act
        garage.CarsByName = new Dictionary<string, Car>();

        // Assert
        Assert.Equal(Parent(holder, nameof(Garage.Cars), 0), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenCollectionItemsMove_ThenParentIndexIsTheNewPosition()
    {
        // Arrange
        var first = new Car();
        var second = new Car();
        var moved = new Car();
        var garage = new Garage(CreateContext()) { Cars = [first, second, moved] };

        // Act
        garage.Cars = [moved, first, second];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 0), Assert.Single(moved.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 1), Assert.Single(first.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 2), Assert.Single(second.GetParents()));
    }

    [Fact]
    public void WhenMovedCollectionItemIsRemoved_ThenParentIsRemoved()
    {
        // Arrange
        var first = new Car();
        var second = new Car();
        var moved = new Car();
        var garage = new Garage(CreateContext()) { Cars = [first, second, moved] };

        garage.Cars = [moved, first, second];

        // Act
        garage.Cars = [first, second];

        // Assert
        Assert.Empty(moved.GetParents());
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
    public void WhenSubjectUnderSeveralKeysIsRemoved_ThenParentIsRemoved()
    {
        // Arrange: attached with the first key, detached with the last one.
        var car = new Car();
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car>();

        // Assert
        Assert.Empty(car.GetParents());
    }

    [Fact]
    public void WhenSubjectListedTwiceInCollectionIsRemoved_ThenParentIsRemoved()
    {
        // Arrange: attached with the first position, detached with the last one.
        var car = new Car();
        var garage = new Garage(CreateContext()) { Cars = [car, car] };

        // Act
        garage.Cars = [];

        // Assert
        Assert.Empty(car.GetParents());
    }

    [Fact]
    public void WhenSubjectMovesToSeveralKeys_ThenParentIndexIsTheFirstKey()
    {
        // Arrange
        var car = new Car();
        var other = new Car();
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["Old"] = car, ["Other"] = other }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car, ["Other"] = other };

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "First"), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenSubjectMovesToSeveralPositions_ThenParentIndexIsTheFirstPosition()
    {
        // Arrange
        var car = new Car();
        var other = new Car();
        var garage = new Garage(CreateContext()) { Cars = [other, car] };

        // Act
        garage.Cars = [car, other, car];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 0), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenRetainedSubjectIsAlsoAppended_ThenParentIndexStaysTheFirstPosition()
    {
        // Arrange
        var car = new Car();
        var other = new Car();
        var garage = new Garage(CreateContext()) { Cars = [car, other] };

        // Act
        garage.Cars = [car, other, car];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 0), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenItemIsRemovedBeforeOthers_ThenFollowingParentIndicesShift()
    {
        // Arrange
        var first = new Car();
        var removed = new Car();
        var third = new Car();
        var fourth = new Car();
        var garage = new Garage(CreateContext()) { Cars = [first, removed, third, fourth] };

        // Act
        garage.Cars = [first, third, fourth];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 0), Assert.Single(first.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 1), Assert.Single(third.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 2), Assert.Single(fourth.GetParents()));
    }

    [Fact]
    public void WhenSubjectLosesItsFirstKey_ThenParentIndexIsTheRemainingKey()
    {
        // Arrange
        var car = new Car();
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["Second"] = car };

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Second"), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenSubjectKeepsOnlyItsLaterKey_ThenParentIndexIsThatKey()
    {
        // Arrange: the remaining key is enumerated at the same place as before.
        var car = new Car();
        var other = new Car();
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["Other"] = other, ["Second"] = car };

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Second"), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenEntryBetweenOthersIsReKeyed_ThenOnlyItsParentIndexChanges()
    {
        // Arrange
        var first = new Car();
        var reKeyed = new Car();
        var last = new Car();
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = first, ["Motor"] = reKeyed, ["Last"] = last }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["First"] = first, ["Engine"] = reKeyed, ["Last"] = last };

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "First"), Assert.Single(first.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Engine"), Assert.Single(reKeyed.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Last"), Assert.Single(last.GetParents()));
    }

    [Fact]
    public void WhenEntryOfReadOnlyDictionaryIsReKeyed_ThenParentIndexIsTheNewKey()
    {
        // Arrange: the wrapper is no IDictionary, so its pairs are found by enumerating it.
        var car = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            BroadCars = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car> { ["Motor"] = car })
        };

        // Act
        holder.BroadCars = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car> { ["Engine"] = car });

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), "Engine"), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenItemOfPlainEnumerableMoves_ThenParentIndexIsTheNewPosition()
    {
        // Arrange: the bag is neither a list nor a collection, so its items are found by enumerating it.
        var car = new Car();
        var other = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext()) { BroadCars = new CarBag(other, car) };

        // Act
        holder.BroadCars = new CarBag(car, other);

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 0), Assert.Single(car.GetParents()));
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 1), Assert.Single(other.GetParents()));
    }

    [Fact]
    public void WhenItemOfMultiDimensionalArrayMoves_ThenParentIndexIsTheNewPosition()
    {
        // Arrange: such an array is a list that cannot be indexed, so its positions are those it enumerates.
        var car = new Car();
        var other = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext()) { BroadCars = new[,] { { other, car } } };

        // Act
        holder.BroadCars = new[,] { { car }, { other } };

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 0), Assert.Single(car.GetParents()));
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 1), Assert.Single(other.GetParents()));
    }

    [Fact]
    public void WhenCollectionIsReplacedByItsOnlyItem_ThenParentIndexIsNull()
    {
        // Arrange
        var car = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext()) { BroadCars = new[] { car } };

        // Act
        holder.BroadCars = car;

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), null), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenSubjectIsReplacedByCollectionHoldingIt_ThenParentIndexIsThePosition()
    {
        // Arrange
        var car = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext()) { BroadCars = car };

        // Act
        holder.BroadCars = new[] { new Car(), car };

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 1), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenDictionaryIsReplacedByItsOnlyEntry_ThenParentIndexIsNull()
    {
        // Arrange
        var car = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            BroadCars = new Dictionary<string, Car> { ["Motor"] = car }
        };

        // Act
        holder.BroadCars = car;

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), null), Assert.Single(car.GetParents()));
    }

    [Fact]
    public void WhenSubjectHeldTwiceLosesOnePositionAndOthersMove_ThenEveryParentIndexIsCurrent()
    {
        // Arrange
        var car = new Car();
        var other = new Car();
        var garage = new Garage(CreateContext()) { Cars = [car, car, other] };

        // Act
        garage.Cars = [other, car];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 1), Assert.Single(car.GetParents()));
        Assert.Equal(Parent(garage, nameof(Garage.Cars), 0), Assert.Single(other.GetParents()));
    }

    [Fact]
    public void WhenValueTypeKeyChanges_ThenParentIndexIsTheNewKey()
    {
        // Arrange
        var kept = new Car();
        var reKeyed = new Car();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            BroadCars = new Dictionary<int, Car> { [1] = kept, [2] = reKeyed }
        };

        // Act
        holder.BroadCars = new Dictionary<int, Car> { [1] = kept, [3] = reKeyed };

        // Assert
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 1), Assert.Single(kept.GetParents()));
        Assert.Equal(Parent(holder, nameof(BroadlyDeclaredHolder.BroadCars), 3), Assert.Single(reKeyed.GetParents()));
    }

    [Fact]
    public void WhenSubjectLeavesOneOfTwoPropertiesOfItsParent_ThenTheOtherParentStays()
    {
        // Arrange
        var car = new Car();
        var garage = new Garage(CreateContext())
        {
            Cars = [car],
            CarsByName = new Dictionary<string, Car> { ["Motor"] = car }
        };

        // Act
        garage.Cars = [];

        // Assert
        Assert.Equal(Parent(garage, nameof(Garage.CarsByName), "Motor"), Assert.Single(car.GetParents()));
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
