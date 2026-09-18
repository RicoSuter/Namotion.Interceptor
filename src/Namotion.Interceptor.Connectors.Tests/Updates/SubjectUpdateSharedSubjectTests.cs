using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies what happens when one wire id is named from two positions of the same update. Which
/// position the applier meets first depends on property order in the payload, so every case is run
/// in both orders.
/// </summary>
public class SubjectUpdateSharedSubjectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenASharedIdReachesAPositionHoldingAnotherInstance_ThenThatInstanceReceivesThePayload(bool membershipFirst)
    {
        // Arrange
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [new Person { FirstName = "Stale" }]
        };
        var existingChild = target.Children[0];
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = InOrder(membershipFirst,
                    nameof(Person.Mother), new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "m" },
                    nameof(Person.Children), new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 1,
                        Items = [new SubjectPropertyItemUpdate { Index = 0, Id = "m" }]
                    }),
                ["m"] = new()
                {
                    [nameof(Person.FirstName)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Eve"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, Assert.Single(target.Children));
        Assert.Equal("Eve", target.Children[0].FirstName);
        Assert.Equal("Eve", target.Mother!.FirstName);
    }

    [Fact]
    public void WhenAPositionHoldingAnotherInstanceReceivesABoundId_ThenALaterReferenceResolvesToTheFirstBinding()
    {
        // Arrange: the applier walks insertion order, so Mother binds a new instance to the ID before the
        // child position that keeps its own instance, and Father is resolved last.
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [new Person { FirstName = "Stale" }]
        };
        var existingChild = target.Children[0];
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = new()
                {
                    [nameof(Person.Mother)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "m" },
                    [nameof(Person.Children)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 1,
                        Items = [new SubjectPropertyItemUpdate { Index = 0, Id = "m" }]
                    },
                    [nameof(Person.Father)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "m" }
                },
                ["m"] = new()
                {
                    [nameof(Person.FirstName)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Eve"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, Assert.Single(target.Children));
        Assert.Equal("Eve", existingChild.FirstName);
        Assert.NotNull(target.Mother);
        Assert.NotSame(existingChild, target.Mother);
        Assert.Same(target.Mother, target.Father);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenOneIdReachesTwoPositionsHoldingEqualButDistinctInstances_ThenEachInstanceReceivesThePayload(bool reversed)
    {
        // Arrange: the two instances compare equal, so only a payload claim by reference identity lets the
        // second one receive the payload the first one already took.
        var first = new ValueEqualWireSubject { EqualityKey = "child" };
        var second = new ValueEqualWireSubject { EqualityKey = "child" };
        var target = new ValueEqualWireSubject(InterceptorSubjectContext.Create().WithRegistry())
        {
            EqualityKey = "root",
            Children = [first, second]
        };
        List<SubjectPropertyItemUpdate> items =
        [
            new SubjectPropertyItemUpdate { Index = 0, Id = "c" },
            new SubjectPropertyItemUpdate { Index = 1, Id = "c" }
        ];
        if (reversed)
        {
            items.Reverse();
        }

        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = new()
                {
                    [nameof(ValueEqualWireSubject.Children)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 2,
                        Items = items
                    }
                },
                ["c"] = new()
                {
                    [nameof(ValueEqualWireSubject.Value)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = 7
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Collection(target.Children,
            child => Assert.Same(first, child),
            child => Assert.Same(second, child));
        Assert.Equal(7, first.Value);
        Assert.Equal(7, second.Value);
    }

    [Fact]
    public void WhenASharedIdCyclesThroughAnotherInstance_ThenTheApplyTerminates()
    {
        // Arrange: the payload of the root's own id is reached again through a child that references
        // itself, so an applier that only compares against the first bound instance never stops.
        var nested = new CycleTestNode { Name = "Stale" };
        nested.Child = nested;
        var target = new CycleTestNode(InterceptorSubjectContext.Create().WithRegistry())
        {
            Name = "Stale",
            Child = nested
        };
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = new()
                {
                    [nameof(CycleTestNode.Name)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Named"
                    },
                    [nameof(CycleTestNode.Child)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "r"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Named", target.Name);
        Assert.Same(nested, target.Child);
        Assert.Equal("Named", nested.Name);
        Assert.Same(nested, nested.Child);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenOneIdNamesTwoDifferentlyTypedProperties_ThenEachGetsAnInstanceOfItsOwnType(bool collectionFirst)
    {
        // Arrange
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry());
        var primary = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "v" };
        var cars = new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Collection,
            Operations =
            [
                new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = 0,
                    Id = "v"
                }
            ]
        };
        var update = new SubjectUpdate
        {
            Root = "f",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["f"] = InOrder(collectionFirst, nameof(MixedTypeFleet.Primary), primary, nameof(MixedTypeFleet.Cars), cars),
                ["v"] = new()
                {
                    [nameof(FleetCar.Label)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Rusty"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Rusty", target.Primary!.Label);
        Assert.Equal("Rusty", Assert.Single(target.Cars).Label);
    }

    [Fact]
    public void WhenAProducedUpdateNamesOneSubjectFromABaseAndADerivedPosition_ThenItsSelfReferenceResolvesAndApplyTerminates()
    {
        // Arrange: the base-typed position binds an instance the derived position cannot hold, and the
        // subject refers to itself through the derived type.
        var trailer = new FleetTrailer { Label = "Flatbed" };
        trailer.Coupled = trailer;
        var source = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry()) { Lead = trailer, Trailer = trailer };
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry());
        var factory = new CountingSubjectFactory();

        // Act
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), factory, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, factory.CreatedSubjects);
        Assert.Same(target.Trailer, target.Trailer!.Coupled);
        Assert.Equal("Flatbed", target.Trailer.Label);
        Assert.Equal("Flatbed", target.Lead!.Label);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnIdThatCannotFitAnItemPositionListsItselfAsItsOwnItem_ThenOneInstanceIsCreatedPerType(bool collectionFirst)
    {
        // Arrange
        var target = new MixedTypeFleet(InterceptorSubjectContext.Create().WithRegistry());
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = InOrder(collectionFirst,
                    nameof(MixedTypeFleet.Primary), new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "x" },
                    nameof(MixedTypeFleet.Cars), new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 1,
                        Items = [new SubjectPropertyItemUpdate { Index = 0, Id = "x" }]
                    }),
                ["x"] = new()
                {
                    [nameof(FleetCar.Towed)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 1,
                        Items = [new SubjectPropertyItemUpdate { Index = 0, Id = "x" }]
                    }
                }
            }
        };
        var factory = new CountingSubjectFactory();

        // Act
        target.ApplySubjectUpdate(update, factory, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, factory.CreatedSubjects);
        Assert.IsType<FleetTruck>(target.Primary);
        var car = Assert.Single(target.Cars);
        Assert.Same(car, Assert.Single(car.Towed));
    }

    [Fact]
    public void WhenOneIdNamesABaseThenALeafThenAMidTypedPosition_ThenTheMidPositionReusesTheLeafInstance()
    {
        // Arrange: the instance created for the base position fits neither other position, and the one
        // created for the leaf position also fits the mid position.
        var target = new LayeredHolder(InterceptorSubjectContext.Create().WithRegistry());
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = new()
                {
                    [nameof(LayeredHolder.Base)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "x" },
                    [nameof(LayeredHolder.Leaf)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "x" },
                    [nameof(LayeredHolder.Mid)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "x" }
                },
                ["x"] = new()
                {
                    [nameof(LayeredBase.Label)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Shared"
                    }
                }
            }
        };
        var factory = new CountingSubjectFactory();

        // Act
        target.ApplySubjectUpdate(update, factory, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, factory.CreatedSubjects);
        Assert.IsType<LayeredLeaf>(target.Leaf);
        Assert.Same(target.Leaf, target.Mid);
        Assert.Equal("Shared", target.Mid!.Label);
        Assert.Equal("Shared", target.Base!.Label);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("list")]
    [InlineData("dictionary")]
    public void WhenAMirrorSharesOneInstanceBetweenTwoPositionsTheSourceHasSplit_ThenACompleteUpdateGivesEachPositionItsOwnSubject(string position)
    {
        // Arrange
        var shared = new Person { FirstName = "Stale" };
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        switch (position)
        {
            case "reference":
                (source.Father, source.Mother) = (first, second);
                (mirror.Father, mirror.Mother) = (shared, shared);
                break;
            case "list":
                source.Children = [first, second];
                mirror.Children = [shared, shared];
                break;
            default:
                source.Relationships = new() { ["first"] = first, ["second"] = second };
                mirror.Relationships = new() { ["first"] = shared, ["second"] = shared };
                break;
        }

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Person[] positions = position switch
        {
            "reference" => [mirror.Father!, mirror.Mother!],
            "list" => [.. mirror.Children],
            _ => [mirror.Relationships!["first"], mirror.Relationships["second"]]
        };
        Assert.Same(shared, positions[0]);
        Assert.Equal(["First", "Second"], positions.Select(person => person.FirstName));
    }

    // Insertion order is what the applier walks, so it decides which position mints the instance.
    private static Dictionary<string, SubjectPropertyUpdate> InOrder(bool reversed,
        string firstName, SubjectPropertyUpdate first, string secondName, SubjectPropertyUpdate second)
        => reversed
            ? new() { [secondName] = second, [firstName] = first }
            : new() { [firstName] = first, [secondName] = second };

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
/// Derived from <see cref="FleetVehicle"/>, so a base-typed position binds an instance this type's own
/// positions cannot hold.
/// </summary>
[InterceptorSubject]
public partial class FleetTrailer : FleetVehicle
{
    public partial FleetTrailer? Coupled { get; set; }
}

/// <summary>
/// Holds one position per level of a three-level hierarchy, declared from the base down to the leaf and
/// then the middle level.
/// </summary>
[InterceptorSubject]
public partial class LayeredHolder
{
    public partial LayeredBase? Base { get; set; }

    public partial LayeredLeaf? Leaf { get; set; }

    public partial LayeredMid? Mid { get; set; }
}

[InterceptorSubject]
public partial class LayeredBase
{
    public partial string? Label { get; set; }
}

[InterceptorSubject]
public partial class LayeredMid : LayeredBase
{
    public partial string? MidLabel { get; set; }
}

[InterceptorSubject]
public partial class LayeredLeaf : LayeredMid
{
    public partial string? LeafLabel { get; set; }
}
