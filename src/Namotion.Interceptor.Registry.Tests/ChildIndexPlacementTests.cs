using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Child placement has two paths: a scan that walks from the slot being filled, and a rebuild that runs
/// when the scan would be quadratic. They must be indistinguishable from the outside, so most of this
/// file drives the same input through both and compares. The existing registry suite cannot reach the
/// rebuild at all, its largest container being ten children against a floor of thirty two.
/// </summary>
public class ChildIndexPlacementTests
{
    private const int ForceScanMinimum = int.MaxValue;
    private const int ForceRebuildMinimum = 0;
    private const int ForceRebuildLimit = 0;
    private const int NeverLimit = int.MaxValue;

    /// <summary>
    /// What a direct call to the registry's placement can affect. The tracked parents behind GetParents are
    /// maintained by ParentTrackingHandler, which only the interceptor drives, so they are covered by the
    /// end-to-end tests further down rather than here, where they could never change.
    /// </summary>
    private sealed record Placement(string[] Children, string[] RegistryParents);

    private sealed record Fixture(
        RegisteredSubjectProperty Property,
        ISubjectRegistry Registry,
        Person[] Attached,
        Person Container);

    private static Fixture Create(int count)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var attached = new Person[count];
        for (var i = 0; i < count; i++)
        {
            attached[i] = new Person { FirstName = "P" + i };
        }

        var container = new Person(context) { Children = attached };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;

        return new Fixture(property, context.TryGetService<ISubjectRegistry>()!, attached, container);
    }

    private static SubjectChildReference[] Incoming(RegisteredSubjectProperty property, IReadOnlyList<Person> order)
    {
        var references = new SubjectChildReference[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            references[i] = new SubjectChildReference(order[i], property.Reference, i);
        }

        return references;
    }

    private static Placement Snapshot(Fixture fixture)
    {
        var children = fixture.Property.Children
            .Select(child => $"{((Person)child.Subject).FirstName}@{child.Index}")
            .ToArray();

        var registryParents = fixture.Attached
            .Select(person =>
            {
                var registered = person.TryGetRegisteredSubject();
                return registered is null || registered.Parents.Length == 0
                    ? $"{person.FirstName}@none"
                    : $"{person.FirstName}@{registered.Parents[0].Index}";
            })
            .ToArray();

        return new Placement(children, registryParents);
    }

    private static IReadOnlyList<Person> Shape(string shape, Person[] attached)
    {
        var order = attached.ToList();
        switch (shape)
        {
            case "SameOrder":
                break;

            case "Reversed":
                order.Reverse();
                break;

            case "RotateForward":
                order.Insert(0, order[^1]);
                order.RemoveAt(order.Count - 1);
                break;

            case "RotateBack":
                order.Add(order[0]);
                order.RemoveAt(0);
                break;

            case "Swaps":
                for (var i = 0; i + 1 < order.Count; i += 2)
                {
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
                }

                break;

            case "LocalSwaps":
                // Two adjacent swaps at the front and nothing else moved: linear for the scan however large
                // the container, so a trigger counting displacements alone would abandon it for no reason.
                for (var i = 0; i + 1 < Math.Min(4, order.Count); i += 2)
                {
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
                }

                break;

            case "Repeat":
                // The same subject at two indices: the first is the one attach recorded and must win.
                order.Insert(order.Count / 2, order[0]);
                break;

            case "Stranded":
                // A child the new value no longer holds, which only an in-place mutation can produce.
                order.RemoveAt(order.Count / 2);
                break;

            case "Absent":
                // A subject the property never held, which neither path may place.
                order.Insert(order.Count / 2, new Person { FirstName = "Ghost" });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        return order;
    }

    private static Placement Place(string shape, int count, bool rebuild)
    {
        var fixture = Create(count);
        var incoming = Incoming(fixture.Property, Shape(shape, fixture.Attached));

        var rebuilt = rebuild
            ? fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceRebuildMinimum, ForceRebuildLimit)
            : fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceScanMinimum, NeverLimit);

        Assert.Equal(rebuild, rebuilt);
        return Snapshot(fixture);
    }

    // SameOrder is absent on purpose: the handover only happens for a child found away from its slot, and
    // that shape has none, so it can never reach the rebuild. It is covered by the scan-only rows of
    // WhenTheDefaultLimitIsUsed and by the end-to-end writes.
    [Theory]
    [InlineData("Reversed")]
    [InlineData("RotateForward")]
    [InlineData("RotateBack")]
    [InlineData("Swaps")]
    [InlineData("Repeat")]
    [InlineData("Stranded")]
    [InlineData("Absent")]
    public void WhenTheSameWriteIsPlacedByEitherPath_ThenChildrenAndBothParentCopiesAgree(string shape)
    {
        // The rebuild is an optimisation, so it has to be undetectable: same order, same indices, and the
        // same values in both copies of the parent index.
        // Arrange & Act
        var scanned = Place(shape, 8, rebuild: false);
        var rebuilt = Place(shape, 8, rebuild: true);

        // Assert
        Assert.Equal(scanned.Children, rebuilt.Children);
        Assert.Equal(scanned.RegistryParents, rebuilt.RegistryParents);
    }

    // Repeat and Absent are left out on purpose: each has a single child costing the remainder of the list,
    // so every limit above zero stays on the scan and the sweep collapses to the one handover the
    // equivalence theory already covers. The shapes below all have many.
    [Theory]
    [InlineData("Reversed")]
    [InlineData("RotateBack")]
    [InlineData("Swaps")]
    [InlineData("Stranded")]
    public void WhenTheHandoverFallsAtAnySlot_ThenTheResultIsTheOneTheScanProduces(string shape)
    {
        // Forcing the rebuild from the first child only ever tests the two paths whole. Sweeping the limit
        // moves the handover through every slot it can fall on, which is where a mistake in the slot or span
        // bookkeeping would drop a child or place it twice.
        // Arrange
        var expected = Place(shape, 12, rebuild: false);
        var handedOver = 0;

        for (var limit = 0; limit <= 12; limit++)
        {
            var fixture = Create(12);
            var incoming = Incoming(fixture.Property, Shape(shape, fixture.Attached));

            // Act
            var rebuilt = fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceRebuildMinimum, limit);

            // Assert
            var actual = Snapshot(fixture);
            Assert.Equal(expected.Children, actual.Children);
            Assert.Equal(expected.RegistryParents, actual.RegistryParents);

            if (rebuilt)
            {
                handedOver++;
            }
        }

        // Without this the sweep passes just as well with the rebuild switched off entirely, and every shape
        // here has more than one child that costs the remainder of the list, so more than one limit must
        // reach it.
        Assert.True(handedOver > 1, $"only {handedOver} of 13 limits handed over");
    }

    [Fact]
    public void WhenTheRebuildReordersAfterChildrenWereRead_ThenTheCachedChildrenAreNotStale()
    {
        // Children hands out a cached snapshot. A rebuild that reorders without invalidating it would serve
        // the old order to every reader from then on.
        // Arrange
        var fixture = Create(4);
        var before = fixture.Property.Children;
        Assert.Equal(["P0", "P1", "P2", "P3"], before.Select(child => ((Person)child.Subject).FirstName));

        var incoming = Incoming(fixture.Property, Shape("Reversed", fixture.Attached));

        // Act
        fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceRebuildMinimum, ForceRebuildLimit);

        // Assert
        Assert.Equal(["P3", "P2", "P1", "P0"], fixture.Property.Children.Select(child => ((Person)child.Subject).FirstName));
    }

    [Fact]
    public void WhenTheRebuildThrowsPartWayThrough_ThenNeitherChildrenNorParentsMoved()
    {
        // A comparer that throws must leave the write as if it had never started: nothing spliced and no
        // parent moved. Both are recorded and applied only once the whole placement has succeeded.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.TryGetService<ISubjectRegistry>()!;
        var displacing = new Person { FirstName = "C" };
        var moving = new Person { FirstName = "A" };
        var exploding = new Person { FirstName = "B" };

        var explodingKey = new ReentrantKey("beta", () => throw new InvalidOperationException("boom"));
        var directory = new PersonDirectory(context)
        {
            Untyped = new Dictionary<ReentrantKey, Person>
            {
                [new ReentrantKey("alpha")] = moving,
                [explodingKey] = exploding,
                [new ReentrantKey("gamma")] = displacing
            }
        };

        var property = directory.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.Untyped))!;

        // The last child first, so it is found away from its slot and the whole write is handed to the
        // rebuild; then a moved index; then the child whose stored key explodes.
        var failing = new[]
        {
            new SubjectChildReference(displacing, property.Reference, new ReentrantKey("z0")),
            new SubjectChildReference(moving, property.Reference, new ReentrantKey("z1")),
            new SubjectChildReference(exploding, property.Reference, new ReentrantKey("z2"))
        };

        explodingKey.Arm();

        // Act
        var exception = Record.Exception(() =>
            property.RefreshChildIndices(failing, registry, ForceRebuildMinimum, ForceRebuildLimit));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(
            ["A@alpha", "B@beta", "C@gamma"],
            property.Children.Select(child => $"{((Person)child.Subject).FirstName}@{child.Index}"));
        Assert.Equal("alpha", moving.TryGetRegisteredSubject()!.Parents[0].Index!.ToString());
        Assert.Equal("gamma", displacing.TryGetRegisteredSubject()!.Parents[0].Index!.ToString());

        // And a later write still lands, from a state nothing has touched.
        var repairing = new[]
        {
            new SubjectChildReference(displacing, property.Reference, new ReentrantKey("omega")),
            new SubjectChildReference(moving, property.Reference, new ReentrantKey("alpha")),
            new SubjectChildReference(exploding, property.Reference, new ReentrantKey("beta"))
        };

        property.RefreshChildIndices(repairing, registry, ForceRebuildMinimum, ForceRebuildLimit);
        Assert.Equal("omega", displacing.TryGetRegisteredSubject()!.Parents[0].Index!.ToString());
    }

    [Fact]
    public void WhenTheRebuildPlacesAReorder_ThenBothParentCopiesFollowTheChildren()
    {
        // The rebuild is the only path a large reorder takes, and nothing in the existing suite reaches it,
        // so the parent index updates it performs are asserted here rather than inferred.
        // Arrange
        var fixture = Create(4);
        var incoming = Incoming(fixture.Property, Shape("Reversed", fixture.Attached));

        // Act
        var rebuilt = fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceRebuildMinimum, ForceRebuildLimit);

        // Assert
        Assert.True(rebuilt);
        Assert.Equal(["P3@0", "P2@1", "P1@2", "P0@3"], Snapshot(fixture).Children);

        Assert.Equal(3, fixture.Attached[0].TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(0, fixture.Attached[3].TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenTheRebuildSeesASubjectTwice_ThenTheFirstIndexWins()
    {
        // Arrange
        var fixture = Create(4);
        var order = fixture.Attached.ToList();
        order.Insert(2, order[0]);
        var incoming = Incoming(fixture.Property, order);

        // Act
        fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceRebuildMinimum, ForceRebuildLimit);

        // Assert
        Assert.Equal(["P0@0", "P1@1", "P2@3", "P3@4"], Snapshot(fixture).Children);
        Assert.Equal(0, fixture.Attached[0].TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenTheRebuildStrandsChildren_ThenTheyKeepTheirRelativeOrderAtTheEnd()
    {
        // Their relative order is read off the children, not off the lookup, whose enumeration order is
        // unspecified and would drift with capacity changes.
        // Arrange
        var fixture = Create(6);
        var order = new List<Person> { fixture.Attached[5], fixture.Attached[3] };
        var incoming = Incoming(fixture.Property, order);

        // Act
        fixture.Property.RefreshChildIndices(incoming, fixture.Registry, ForceRebuildMinimum, ForceRebuildLimit);

        // Assert
        Assert.Equal(["P5@0", "P3@1", "P0@0", "P1@1", "P2@2", "P4@4"], Snapshot(fixture).Children);
    }

    [Fact]
    public void WhenChildrenAreEqualByValueButDistinct_ThenTheRebuildKeepsThemApart()
    {
        // Placement keys on identity. A lookup built with the default comparer would collapse these two
        // into one entry, or throw on the duplicate key, inside a property write.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var container = new PersonDirectory(context);
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.Untyped))!;

        var first = new EqualByValueItem { Tag = "first" };
        var second = new EqualByValueItem { Tag = "second" };
        property.AddChild(new SubjectPropertyChild { Subject = first, Index = 0 });
        property.AddChild(new SubjectPropertyChild { Subject = second, Index = 1 });

        var incoming = new[]
        {
            new SubjectChildReference(second, property.Reference, 0),
            new SubjectChildReference(first, property.Reference, 1)
        };

        // Act
        var exception = Record.Exception(() =>
            property.RefreshChildIndices(incoming, context.TryGetService<ISubjectRegistry>()!, ForceRebuildMinimum, ForceRebuildLimit));

        // Assert
        Assert.Null(exception);
        Assert.Equal(
            ["second@0", "first@1"],
            property.Children.Select(child => $"{((EqualByValueItem)child.Subject).Tag}@{child.Index}"));
    }

    [Theory]
    [InlineData("SameOrder", 64, false)]
    [InlineData("RotateForward", 64, false)]
    [InlineData("LocalSwaps", 1000, false)]
    [InlineData("Reversed", 64, true)]
    [InlineData("RotateBack", 64, true)]
    [InlineData("Swaps", 64, true)]
    [InlineData("Reversed", 16, false)]
    [InlineData("SameOrder", 16, false)]
    public void WhenTheDefaultLimitIsUsed_ThenOnlyQuadraticShapesRebuild(string shape, int count, bool expected)
    {
        // Rotating by one and a handful of local swaps are linear for the scan however large the container,
        // so they must stay on it; reversing and rotating the other way are not, so they must not. Below the
        // floor nothing rebuilds, because the scan wins there whatever the shape.
        // Arrange
        var fixture = Create(count);
        var incoming = Incoming(fixture.Property, Shape(shape, fixture.Attached));

        // Act
        var rebuilt = fixture.Property.RefreshChildIndices(incoming, fixture.Registry,
            RegisteredSubjectProperty.RebuildMinimumChildren, RegisteredSubjectProperty.RebuildCostlyChildLimit);

        // Assert
        Assert.Equal(expected, rebuilt);
    }

    [Fact]
    public void WhenASubjectIsHeldUnderManyKeys_ThenTheRepeatedMissesStillTriggerTheRebuild()
    {
        // A miss walks the whole remainder and displaces nothing, so a budget that only counted
        // displacements would let this stay quadratic on the scan.
        // An unbroken run, with no hit after it to carry the handover: testing the limit after the miss was
        // skipped would scan to the end for every one of these and never hand over.
        // Arrange
        var fixture = Create(64);
        var order = new List<Person> { fixture.Attached[0] };
        for (var i = 0; i < 5000; i++)
        {
            order.Add(fixture.Attached[0]);
        }

        var incoming = Incoming(fixture.Property, order);

        // Act
        var rebuilt = fixture.Property.RefreshChildIndices(incoming, fixture.Registry,
            RegisteredSubjectProperty.RebuildMinimumChildren, RegisteredSubjectProperty.RebuildCostlyChildLimit);

        // Assert
        Assert.True(rebuilt);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    public void WhenACollectionIsReversedByARealWrite_ThenChildrenAndBothParentCopiesFollow(int count)
    {
        // Driven through the interceptor rather than by calling the registry directly, so both handlers run
        // and the tracked parents are exercised. 8 stays on the scan, 64 reversed crosses into the rebuild,
        // and the observable result has to be the same either way.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var children = new Person[count];
        for (var i = 0; i < count; i++)
        {
            children[i] = new Person { FirstName = "P" + i };
        }

        var container = new Person(context) { Children = children };

        // Act
        container.Children = children.Reverse().ToArray();

        // Assert
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;
        Assert.Equal(children.Reverse(), property.Children.Select(child => child.Subject));
        Assert.Equal(Enumerable.Range(0, count).Cast<object>(), property.Children.Select(child => child.Index));

        for (var i = 0; i < count; i++)
        {
            var expected = count - 1 - i;
            Assert.Equal(expected, children[i].TryGetRegisteredSubject()!.Parents[0].Index);
            Assert.Equal(expected, children[i].GetParents().Single().Index);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    public void WhenADictionaryIsReorderedByARealWrite_ThenKeysStayWithTheirSubjects(int count)
    {
        // Same keys, reversed enumeration order. No index changes at all, yet every child moves, which is
        // the shape that made the placement quadratic.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var people = new Person[count];
        var forward = new Dictionary<string, Person>();
        for (var i = 0; i < count; i++)
        {
            people[i] = new Person { FirstName = "P" + i };
            forward["k" + i] = people[i];
        }

        var directory = new PersonDirectory(context) { PeopleByName = forward };

        var reversed = new Dictionary<string, Person>();
        for (var i = count - 1; i >= 0; i--)
        {
            reversed["k" + i] = people[i];
        }

        // Act
        directory.PeopleByName = reversed;

        // Assert
        for (var i = 0; i < count; i++)
        {
            Assert.Equal("k" + i, people[i].TryGetRegisteredSubject()!.Parents[0].Index);
            Assert.Equal("k" + i, people[i].GetParents().Single().Index);
        }

        var property = directory.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.PeopleByName))!;
        Assert.Equal(people.Reverse(), property.Children.Select(child => child.Subject));
    }

    [Fact]
    public void WhenAReorderedCollectionThenLosesItems_ThenNoChildOrParentEntryIsLeftBehind()
    {
        // The reorder runs through the rebuild, and the removal that follows has to still find every child,
        // which it can only do if the rebuild left the stored indices consistent.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var children = new Person[64];
        for (var i = 0; i < children.Length; i++)
        {
            children[i] = new Person { FirstName = "P" + i };
        }

        var container = new Person(context) { Children = children };
        container.Children = children.Reverse().ToArray();

        // Act
        var kept = children.Take(4).Reverse().ToArray();
        container.Children = kept;

        // Assert
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;
        Assert.Equal(kept, property.Children.Select(child => child.Subject));

        foreach (var removed in children.Skip(4))
        {
            Assert.Null(removed.TryGetRegisteredSubject());
            Assert.Empty(removed.GetParents());
        }

        for (var i = 0; i < kept.Length; i++)
        {
            Assert.Equal(i, kept[i].TryGetRegisteredSubject()!.Parents[0].Index);
            Assert.Equal(i, kept[i].GetParents().Single().Index);
        }
    }

    [Fact]
    public void WhenAKeyComparerThrowsDuringTheRebuild_ThenTheChildrenAreNotLeftTorn()
    {
        // Equality on a dictionary key is caller code. If it throws mid rebuild the children must still be
        // the ones from before the write rather than a half applied mixture.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        var exploding = new ReentrantKey("alpha", () => throw new InvalidOperationException("boom"));
        var container = new PersonDirectory(context)
        {
            Untyped = new Dictionary<ReentrantKey, Person> { [exploding] = first, [new ReentrantKey("beta")] = second }
        };

        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.Untyped))!;
        var before = property.Children.Select(child => $"{((Person)child.Subject).FirstName}@{child.Index}").ToArray();

        var incoming = new[]
        {
            new SubjectChildReference(second, property.Reference, new ReentrantKey("beta")),
            new SubjectChildReference(first, property.Reference, new ReentrantKey("gamma"))
        };

        exploding.Arm();

        // Act
        var exception = Record.Exception(() =>
            property.RefreshChildIndices(incoming, context.TryGetService<ISubjectRegistry>()!, ForceRebuildMinimum, ForceRebuildLimit));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(before, property.Children.Select(child => $"{((Person)child.Subject).FirstName}@{child.Index}"));
    }

    [Fact]
    public void WhenAKeyComparerReentersTheRefresh_ThenBothPropertiesArePlacedCorrectly()
    {
        // The rebuild buffers are per thread, so a nested refresh reached through caller equality must build
        // its own rather than clear the ones the outer call is filling.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.TryGetService<ISubjectRegistry>()!;

        var outer = Create(4);
        var nested = Create(4);
        var nestedIncoming = Incoming(nested.Property, Shape("Reversed", nested.Attached));

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        var reentrant = new ReentrantKey("alpha", () =>
            nested.Property.RefreshChildIndices(nestedIncoming, nested.Registry, ForceRebuildMinimum, ForceRebuildLimit));

        var container = new PersonDirectory(context)
        {
            Untyped = new Dictionary<ReentrantKey, Person> { [reentrant] = first, [new ReentrantKey("beta")] = second }
        };

        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.Untyped))!;

        var incoming = new[]
        {
            new SubjectChildReference(second, property.Reference, new ReentrantKey("beta")),
            new SubjectChildReference(first, property.Reference, new ReentrantKey("alpha"))
        };

        reentrant.Arm();

        // Act
        property.RefreshChildIndices(incoming, registry, ForceRebuildMinimum, ForceRebuildLimit);

        // Assert
        Assert.Equal(
            ["B@beta", "A@alpha"],
            property.Children.Select(child => $"{((Person)child.Subject).FirstName}@{child.Index}"));

        Assert.Equal(["P3@0", "P2@1", "P1@2", "P0@3"], Snapshot(nested).Children);
    }
}
