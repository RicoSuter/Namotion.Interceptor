using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ChildIndexHandlerTests
{
    private static (IInterceptorSubjectContext Context, RecordingChildIndexHandler Handler) CreateContext()
    {
        var handler = new RecordingChildIndexHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => handler);

        return (context, handler);
    }

    [Fact]
    public void WhenDictionaryIsAssignedAgainWithTheSameEntries_ThenHandlerIsNotCalled()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var first = new Car();
        var second = new Car();
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = first, ["Second"] = second }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["First"] = first, ["Second"] = second };

        // Assert
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void WhenDictionaryIsRebuiltInAnotherOrderWithTheSameKeys_ThenHandlerIsNotCalled()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var first = new Car();
        var second = new Car();
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = first, ["Second"] = second }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["Second"] = second, ["First"] = first };

        // Assert
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void WhenDictionaryEntryIsReKeyed_ThenHandlerGetsOnlyThatChildOnce()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var first = new Car();
        var child = new Car();
        var last = new Car();
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["First"] = first, ["Motor"] = child, ["Last"] = last }
        };

        var reKeyed = new Dictionary<string, Car>(garage.CarsByName);
        reKeyed.Remove("Motor");
        reKeyed["Engine"] = child;

        // Act
        garage.CarsByName = reKeyed;

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal(new PropertyReference(garage, nameof(Garage.CarsByName)), call.Property);
        Assert.Equal([new SubjectChild(child, "Engine")], call.Children);
    }

    [Fact]
    public void WhenItemIsInsertedAhead_ThenHandlerGetsTheShiftedItemsButNotTheInsertedOne()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var first = new Car();
        var second = new Car();
        var inserted = new Car();
        var garage = new Garage(context) { Cars = [first, second] };

        // Act
        garage.Cars = [inserted, first, second];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(first, 1), new SubjectChild(second, 2)], call.Children);
    }

    [Fact]
    public void WhenSubjectIsListedTwice_ThenHandlerGetsAllItsPositionsAsOneRun()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var other = new Car();
        var shared = new Car();
        var garage = new Garage(context) { Cars = [other, shared] };

        // Act
        garage.Cars = [shared, other, shared];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(shared, 0), new SubjectChild(shared, 2), new SubjectChild(other, 1)], call.Children);
    }

    [Fact]
    public void WhenDictionaryGainsAnotherKeyForASubjectItHolds_ThenHandlerGetsBothKeys()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context) { CarsByName = new Dictionary<string, Car> { ["First"] = car } };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "First"), new SubjectChild(car, "Second")], call.Children);
    }

    [Fact]
    public void WhenDictionaryLosesTheSecondOfTwoKeysForASubject_ThenHandlerGetsTheRemainingKey()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context) { CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car } };
        handler.Calls.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["First"] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "First")], call.Children);
    }

    [Fact]
    public void WhenListGainsAnotherPositionForASubjectItHolds_ThenHandlerGetsBothPositions()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context) { Cars = [car, other] };

        // Act
        garage.Cars = [car, other, car];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, 0), new SubjectChild(car, 2)], call.Children);
    }

    [Fact]
    public void WhenListLosesTheSecondOfTwoPositionsOfASubject_ThenHandlerGetsTheRemainingPosition()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context) { Cars = [car, car] };
        handler.Calls.Clear();

        // Act
        garage.Cars = [car];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, 0)], call.Children);
    }

    [Fact]
    public void WhenDictionaryReplacesTheSecondKeyOfASubject_ThenHandlerGetsBothKeys()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context) { CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Second"] = car } };
        handler.Calls.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["First"] = car, ["Third"] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "First"), new SubjectChild(car, "Third")], call.Children);
    }

    [Fact]
    public void WhenListMovesTheSecondPositionOfASubject_ThenHandlerGetsItsPositionsAndTheShiftedSubject()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context) { Cars = [car, car, other] };
        handler.Calls.Clear();

        // Act
        garage.Cars = [car, other, car];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, 0), new SubjectChild(car, 2), new SubjectChild(other, 1)], call.Children);
    }

    [Fact]
    public void WhenListHoldingSubjectsTwiceIsReorderedWithoutMovingTheirFirstPositions_ThenHandlerGetsBothWithAllTheirPositions()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var first = new Car();
        var second = new Car();
        var garage = new Garage(context) { Cars = [first, second, second, first] };
        handler.Calls.Clear();

        // Act
        garage.Cars = [first, second, first, second];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(first, 0), new SubjectChild(first, 2), new SubjectChild(second, 1), new SubjectChild(second, 3)], call.Children);
    }

    [Fact]
    public void WhenListHoldingASubjectTwiceIsAssignedAgainWithTheSameItems_ThenHandlerIsNotCalled()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context) { Cars = [car, other, car] };
        handler.Calls.Clear();

        // Act
        garage.Cars = [car, other, car];

        // Assert
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void WhenDictionaryHoldingASubjectTwiceIsRebuiltInAnotherOrderThatKeepsTheKeysOfEachSubjectInOrder_ThenHandlerIsNotCalled()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["A"] = car, ["B"] = car, ["C"] = other }
        };
        handler.Calls.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["A"] = car, ["C"] = other, ["B"] = car };

        // Assert
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void WhenDictionaryReordersTheLaterKeysOfASubject_ThenHandlerGetsItsKeysInTheNewOrder()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["A"] = car, ["B"] = car, ["C"] = car }
        };
        handler.Calls.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["A"] = car, ["C"] = car, ["B"] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "A"), new SubjectChild(car, "C"), new SubjectChild(car, "B")], call.Children);
    }

    [Fact]
    public void WhenDictionaryComparedByReferenceLosesALaterKeyEqualToTheFirst_ThenHandlerGetsTheSubject()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var firstKey = new string('A', 1);
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car>(ReferenceEqualityComparer.Instance)
            {
                [firstKey] = car, [new string('A', 1)] = car
            }
        };
        handler.Calls.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car>(ReferenceEqualityComparer.Instance) { [firstKey] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "A")], call.Children);
    }

    [Fact]
    public void WhenCollectionGivesWayToItsOnlyItem_ThenHandlerGetsItWithoutIndex()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var holder = new BroadlyDeclaredHolder(context) { BroadCars = new List<Car> { car } };

        // Act
        holder.BroadCars = car;

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, null)], call.Children);
    }

    [Fact]
    public void WhenHandlerMovesAnotherPropertyDuringTheCall_ThenTheOuterCallKeepsItsChildren()
    {
        // Arrange
        var first = new Car();
        var second = new Car();
        var handler = new NestedWritingChildIndexHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => handler);
        var garage = new Garage(context)
        {
            Cars = [first, second],
            CarsByName = new Dictionary<string, Car> { ["A"] = first }
        };
        handler.NestedWrite = () => garage.CarsByName = new Dictionary<string, Car> { ["B"] = first };

        // Act
        garage.Cars = [second, first];

        // Assert
        var outer = handler.Calls.Single(call => call.Property.Name == nameof(Garage.Cars));
        Assert.Equal([new SubjectChild(second, 0), new SubjectChild(first, 1)], outer.Before);
        Assert.Equal(outer.Before, outer.After);
        Assert.Contains(handler.Calls, call => call.Property.Name == nameof(Garage.CarsByName));
    }

    [Fact]
    public void WhenSubjectUnderTwoKeysIsReordered_ThenHandlerGetsBothKeysInTheNewOrder()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["P"] = car, ["Q"] = car }
        };
        handler.Calls.Clear();

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["Q"] = car, ["P"] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "Q"), new SubjectChild(car, "P")], call.Children);
    }

    [Fact]
    public void WhenKeyEqualsThrows_ThenThatWriteMissesItsChangeAndTheNextWriteIsReconciled()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var holder = new BroadlyDeclaredHolder(context)
        {
            BroadCars = new Dictionary<CastingKey, Car> { [new CastingKey()] = car }
        };
        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act & Assert: the old key is compared with a key of another type, and its Equals casts.
        var stored = new Dictionary<string, Car> { ["A"] = car };
        Assert.Throws<InvalidCastException>(() => holder.BroadCars = stored);
        Assert.Same(stored, holder.BroadCars);
        Assert.Empty(changes);

        holder.BroadCars = new Dictionary<string, Car> { ["B"] = car };
        Assert.Single(changes);
        Assert.Equal([new SubjectChild(car, "B")], handler.Calls[^1].Children);
    }

    [Fact]
    public void WhenSubjectIsAttachedAtTwoPositions_ThenHandlerGetsBothPositions()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context) { Cars = [other] };

        // Act
        garage.Cars = [other, car, car];

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, 1), new SubjectChild(car, 2)], call.Children);
    }

    [Fact]
    public void WhenSubjectIsAttachedAtTwoKeys_ThenHandlerGetsBothKeys()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context);

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["A"] = car, ["B"] = car };

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal([new SubjectChild(car, "A"), new SubjectChild(car, "B")], call.Children);
    }

    private sealed class CastingKey
    {
        public override bool Equals(object? obj) => ReferenceEquals(this, (CastingKey)obj!);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    private sealed class NestedWritingChildIndexHandler : IChildIndexHandler
    {
        public Action? NestedWrite { get; set; }

        public List<(PropertyReference Property, SubjectChild[] Before, SubjectChild[] After)> Calls { get; } = [];

        public void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
        {
            var before = children.ToArray();
            var nestedWrite = NestedWrite;
            NestedWrite = null;
            nestedWrite?.Invoke();
            Calls.Add((property, before, children.ToArray()));
        }
    }

    [Fact]
    public void WhenParentHoldingASubjectTwiceIsAttached_ThenHandlerGetsBothPositions()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage { Cars = [car, car] };
        var holder = new BroadlyDeclaredHolder(context);

        // Act
        holder.BroadCars = garage;

        // Assert
        var call = Assert.Single(handler.Calls);
        Assert.Equal(new PropertyReference(garage, nameof(Garage.Cars)), call.Property);
        Assert.Equal([new SubjectChild(car, 0), new SubjectChild(car, 1)], call.Children);
    }

    [Fact]
    public void WhenParentHoldingEachSubjectOnceIsAttached_ThenHandlerIsNotCalled()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var garage = new Garage { Cars = [new Car(), new Car()] };
        var holder = new BroadlyDeclaredHolder(context);

        // Act
        holder.BroadCars = garage;

        // Assert
        Assert.Empty(handler.Calls);
    }

    private sealed class RecordingChildIndexHandler : IChildIndexHandler
    {
        public List<(PropertyReference Property, SubjectChild[] Children)> Calls { get; } = [];

        public void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
        {
            Calls.Add((property, children.ToArray()));
        }
    }
}
