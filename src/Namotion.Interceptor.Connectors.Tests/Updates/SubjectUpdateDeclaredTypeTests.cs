using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that an ID names one subject, which is never assigned to a position whose declared type it does
/// not fit. The receiver creates a subject from the declared type of the position that first names it, so a
/// position of an unrelated or more derived type fails instead of receiving a second instance under the same ID.
/// </summary>
public class SubjectUpdateDeclaredTypeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenOneIdNamesTwoDifferentlyTypedPositions_ThenTheFirstHoldsItAndTheOtherFails(bool collectionFirst)
    {
        // Arrange
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry());
        var primary = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "v" };
        var cars = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Collection, Items = [new SubjectPropertyItemUpdate { Id = "v" }] };
        var update = new SubjectUpdate
        {
            Root = "f",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["f"] = collectionFirst
                    ? new() { [nameof(MixedTypeFleet.Cars)] = cars, [nameof(MixedTypeFleet.Primary)] = primary }
                    : new() { [nameof(MixedTypeFleet.Primary)] = primary, [nameof(MixedTypeFleet.Cars)] = cars },
                ["v"] = new() { [nameof(FleetCar.Label)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Rusty" } }
            }
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Contains("'v'", exception.Message);
        if (collectionFirst)
        {
            Assert.Equal("Rusty", Assert.Single(target.Cars).Label);
            Assert.Null(target.Primary);
        }
        else
        {
            Assert.Equal("Rusty", target.Primary!.Label);
            Assert.Empty(target.Cars);
        }
    }

    [Fact]
    public void WhenAResolvedSubjectDoesNotFitTheDeclaredType_ThenItIsNotAssigned()
    {
        // Arrange
        var car = new FleetCar { Label = "Car" };
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry()) { Cars = [car] };
        car.SetSubjectId("car");
        var update = new SubjectUpdate
        {
            Root = "f",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["f"] = new() { [nameof(MixedTypeFleet.Primary)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "car" } }
            },
            CompleteSubjectIds = []
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));
        Assert.Null(target.Primary);
        Assert.Same(car, Assert.Single(target.Cars));
    }

    [Fact]
    public void WhenAProducedUpdateNamesOneSubjectFromABaseAndADerivedPosition_ThenTheSubjectIsCreatedOnceAndTheDerivedPositionFails()
    {
        // Arrange: the base-typed position is applied first and creates the subject from its declared type,
        // which the derived position cannot hold; the subject also refers to itself through the derived type.
        var trailer = new FleetTrailer { Label = "Flatbed" };
        trailer.Coupled = trailer;
        var source = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry()) { Lead = trailer, Trailer = trailer };
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry());
        var factory = new CountingSubjectFactory();

        // Act
        Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), factory, ChangeOrigin.Local));

        // Assert
        Assert.Equal(1, factory.CreatedSubjects);
        Assert.Equal("Flatbed", target.Lead!.Label);
        Assert.Null(target.Trailer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnIdThatCannotFitAnItemPositionListsItselfAsItsOwnItem_ThenTheApplyTerminatesWithOneInstance(bool collectionFirst)
    {
        // Arrange
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry());
        var primary = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "x" };
        var cars = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Collection, Items = [new SubjectPropertyItemUpdate { Id = "x" }] };
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = collectionFirst
                    ? new() { [nameof(MixedTypeFleet.Cars)] = cars, [nameof(MixedTypeFleet.Primary)] = primary }
                    : new() { [nameof(MixedTypeFleet.Primary)] = primary, [nameof(MixedTypeFleet.Cars)] = cars },
                ["x"] = new()
                {
                    [nameof(FleetCar.Towed)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items = [new SubjectPropertyItemUpdate { Id = "x" }]
                    }
                }
            }
        };
        var factory = new CountingSubjectFactory();

        // Act
        Assert.Throws<InvalidOperationException>(() => target.ApplySubjectUpdate(update, factory, ChangeOrigin.Local));

        // Assert
        Assert.Equal(1, factory.CreatedSubjects);
        if (collectionFirst)
        {
            var car = Assert.Single(target.Cars);
            Assert.Same(car, Assert.Single(car.Towed));
            Assert.Null(target.Primary);
        }
        else
        {
            Assert.IsType<FleetTruck>(target.Primary);
            Assert.Empty(target.Cars);
        }
    }

    /// <summary>
    /// Counts the subjects it creates and stops a runaway apply long before it could exhaust the stack.
    /// </summary>
    private sealed class CountingSubjectFactory : ISubjectFactory
    {
        public int CreatedSubjects { get; private set; }

        public IInterceptorSubject CreateSubject(Type type, IServiceProvider? serviceProvider)
            => ++CreatedSubjects > 20
                ? throw new InvalidOperationException("The apply created subjects without bound.")
                : DefaultSubjectFactory.Instance.CreateSubject(type, serviceProvider);

        public IEnumerable<IInterceptorSubject?> CreateSubjectCollection(Type propertyType, params IEnumerable<IInterceptorSubject?> children)
            => DefaultSubjectFactory.Instance.CreateSubjectCollection(propertyType, children);

        public System.Collections.IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries)
            => DefaultSubjectFactory.Instance.CreateSubjectDictionary(propertyType, entries);
    }
}

/// <summary>
/// Model whose reference and collection hold unrelated subject types, so one wire id naming both
/// positions cannot be satisfied by a single instance.
/// </summary>
[InterceptorSubject]
public partial class MixedTypeFleet
{
    public MixedTypeFleet()
    {
        Cars = [];
    }

    public partial FleetTruck? Primary { get; set; }

    public partial List<FleetCar> Cars { get; set; }

    public partial FleetVehicle? Lead { get; set; }

    public partial FleetTrailer? Trailer { get; set; }
}

[InterceptorSubject]
public partial class FleetCar
{
    public FleetCar()
    {
        Towed = [];
    }

    public partial string? Label { get; set; }

    public partial List<FleetCar> Towed { get; set; }
}

[InterceptorSubject]
public partial class FleetTruck
{
    public partial string? Label { get; set; }
}

[InterceptorSubject]
public partial class FleetVehicle
{
    public partial string? Label { get; set; }
}

/// <summary>
/// Derived from <see cref="FleetVehicle"/>, so a base-typed position creates an instance this type's own
/// positions cannot hold.
/// </summary>
[InterceptorSubject]
public partial class FleetTrailer : FleetVehicle
{
    public partial FleetTrailer? Coupled { get; set; }
}
