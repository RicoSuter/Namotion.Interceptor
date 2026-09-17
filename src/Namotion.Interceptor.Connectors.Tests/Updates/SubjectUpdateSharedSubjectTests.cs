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
                ["r"] = CreateRootProperties(membershipFirst),
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
        var rootProperties = new Dictionary<string, SubjectPropertyUpdate>();
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

        // Insertion order is what the applier walks, so it decides which position mints the instance.
        if (collectionFirst)
        {
            rootProperties[nameof(MixedTypeFleet.Cars)] = cars;
            rootProperties[nameof(MixedTypeFleet.Primary)] = primary;
        }
        else
        {
            rootProperties[nameof(MixedTypeFleet.Primary)] = primary;
            rootProperties[nameof(MixedTypeFleet.Cars)] = cars;
        }

        var update = new SubjectUpdate
        {
            Root = "f",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["f"] = rootProperties,
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

    private static Dictionary<string, SubjectPropertyUpdate> CreateRootProperties(bool membershipFirst)
    {
        var properties = new Dictionary<string, SubjectPropertyUpdate>();
        var mother = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "m" };
        var children = new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Collection,
            Count = 1,
            Items = [new SubjectPropertyItemUpdate { Index = 0, Id = "m" }]
        };

        // Insertion order is what the applier walks, so it decides which position mints the instance.
        if (membershipFirst)
        {
            properties[nameof(Person.Children)] = children;
            properties[nameof(Person.Mother)] = mother;
        }
        else
        {
            properties[nameof(Person.Mother)] = mother;
            properties[nameof(Person.Children)] = children;
        }

        return properties;
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
}

[InterceptorSubject]
public partial class FleetCar
{
    public partial string? Label { get; set; }
}

[InterceptorSubject]
public partial class FleetTruck
{
    public partial string? Label { get; set; }
}
