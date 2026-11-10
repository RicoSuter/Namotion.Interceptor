using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The attach of a subject that a value holds at more than one key or position carries the later ones, so that a
/// handler stores every place of the subject from the attach alone.
/// </summary>
public class AdditionalIndicesTests
{
    private static (IInterceptorSubjectContext Context, RecordingLifecycleHandler Handler) CreateContext()
    {
        var handler = new RecordingLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents()
            .WithService(() => handler);

        return (context, handler);
    }

    private static SubjectParent Parent(IInterceptorSubject subject, string propertyName, object? index)
    {
        return new SubjectParent(new PropertyReference(subject, propertyName), index);
    }

    [Fact]
    public void WhenChangeIsBuiltWithoutAdditionalIndices_ThenTheyAreEmpty()
    {
        // Arrange
        var change = new SubjectLifecycleChange { Subject = new Car(), ReferenceCount = 1 };

        // Act
        var additionalIndices = change.AdditionalIndices;

        // Assert
        Assert.False(additionalIndices.IsDefault);
        Assert.Empty(additionalIndices);
    }

    [Fact]
    public void WhenWriteAttachesSubjectAtTwoPositions_ThenItsAttachCarriesTheLaterPosition()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context) { Cars = [other] };

        // Act
        garage.Cars = [other, car, car];

        // Assert
        var attach = Assert.Single(handler.GetAdded(car));
        Assert.Equal(1, attach.Index);
        Assert.Equal<object?>([2], attach.AdditionalIndices);
        Assert.Equal<SubjectParent>([Parent(garage, nameof(Garage.Cars), 1), Parent(garage, nameof(Garage.Cars), 2)], car.GetParents());
    }

    [Fact]
    public void WhenWriteAttachesSubjectAtThreePositions_ThenItsAttachCarriesTheLaterPositionsInOrder()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage(context);

        // Act
        garage.Cars = [car, other, car, car];

        // Assert
        var attach = Assert.Single(handler.GetAdded(car));
        Assert.Equal(0, attach.Index);
        Assert.Equal<object?>([2, 3], attach.AdditionalIndices);
        Assert.Empty(Assert.Single(handler.GetAdded(other)).AdditionalIndices);
    }

    [Fact]
    public void WhenWriteAttachesSubjectAtTwoKeys_ThenItsAttachCarriesTheLaterKey()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context);

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["A"] = car, ["B"] = car };

        // Assert
        var attach = Assert.Single(handler.GetAdded(car));
        Assert.Equal("A", attach.Index);
        Assert.Equal<object?>(["B"], attach.AdditionalIndices);
        Assert.Equal<SubjectParent>([Parent(garage, nameof(Garage.CarsByName), "A"), Parent(garage, nameof(Garage.CarsByName), "B")], car.GetParents());
    }

    [Fact]
    public void WhenSubjectWhosePropertyRepeatsAChildIsAttachedLater_ThenTheChildAttachCarriesTheLaterPosition()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage { Cars = [car, car] };
        var holder = new BroadlyDeclaredHolder(context);

        // Act
        holder.BroadCars = garage;

        // Assert
        var attach = Assert.Single(handler.GetAdded(car));
        Assert.Equal(new PropertyReference(garage, nameof(Garage.Cars)), attach.Property);
        Assert.Equal(0, attach.Index);
        Assert.Equal<object?>([1], attach.AdditionalIndices);
        Assert.Equal<SubjectParent>([Parent(garage, nameof(Garage.Cars), 0), Parent(garage, nameof(Garage.Cars), 1)], car.GetParents());
    }

    [Fact]
    public void WhenSubjectWithTwoPropertiesThatRepeatAChildIsAttachedLater_ThenEachAttachCarriesTheLaterPlaceOfItsProperty()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage
        {
            Cars = [car, car],
            CarsByName = new Dictionary<string, Car> { ["A"] = car, ["B"] = car }
        };
        var holder = new BroadlyDeclaredHolder(context);

        // Act
        holder.BroadCars = garage;

        // Assert
        var attaches = handler.GetAdded(car);
        Assert.Equal(2, attaches.Count);
        var listAttach = attaches.Single(change => change.Property!.Value.Name == nameof(Garage.Cars));
        Assert.Equal(0, listAttach.Index);
        Assert.Equal<object?>([1], listAttach.AdditionalIndices);
        var dictionaryAttach = attaches.Single(change => change.Property!.Value.Name == nameof(Garage.CarsByName));
        Assert.Equal("A", dictionaryAttach.Index);
        Assert.Equal<object?>(["B"], dictionaryAttach.AdditionalIndices);
        Assert.Equal(4, car.GetParents().Length);
    }

    [Fact]
    public void WhenSubjectIsAttachedOncePerProperty_ThenItsAttachesCarryNoAdditionalIndices()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var other = new Car();
        var garage = new Garage
        {
            Cars = [car, other],
            CarsByName = new Dictionary<string, Car> { ["A"] = car }
        };
        var holder = new BroadlyDeclaredHolder(context);

        // Act
        holder.BroadCars = garage;
        garage.Cars = [car, other, new Car()];

        // Assert
        Assert.All(handler.Changes.Where(change => change.IsPropertyReferenceAdded),
            change => Assert.Empty(change.AdditionalIndices));
        Assert.Equal(2, handler.GetAdded(car).Count);
    }

    [Fact]
    public void WhenSubjectHeldTwiceIsDetached_ThenTheDetachCarriesNoAdditionalIndices()
    {
        // Arrange
        var (context, handler) = CreateContext();
        var car = new Car();
        var garage = new Garage(context) { Cars = [car, car] };

        // Act
        garage.Cars = [];

        // Assert
        var detach = Assert.Single(handler.Changes, change => change.IsPropertyReferenceRemoved && change.Subject == car);
        Assert.Empty(detach.AdditionalIndices);
        Assert.Empty(car.GetParents());
    }

    private sealed class RecordingLifecycleHandler : ILifecycleHandler
    {
        public List<SubjectLifecycleChange> Changes { get; } = [];

        public List<SubjectLifecycleChange> GetAdded(IInterceptorSubject subject)
        {
            return Changes.Where(change => change.IsPropertyReferenceAdded && change.Subject == subject).ToList();
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            Changes.Add(change);
        }
    }
}
