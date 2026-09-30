using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies where the producer marks <see cref="SubjectPropertyUpdate.Mode"/>, and that a receiver keyed on
/// it reaches the source's state where the unmarked wire shape is ambiguous.
/// </summary>
public class SubjectUpdateModeTests
{
    [Fact]
    public void WhenACompleteUpdateIsCreated_ThenEveryContainerIsMarkedCompleteAndNoReferenceIsMarkedReplaced()
    {
        // Arrange
        var child = new Person { FirstName = "Child" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Father = child,
            Children = [child],
            Relationships = new() { ["child"] = child }
        };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        var propertyUpdates = update.Subjects.Values.SelectMany(properties => properties.Values).ToList();
        var containers = propertyUpdates
            .Where(propertyUpdate => propertyUpdate.Kind is SubjectPropertyUpdateKind.Collection or SubjectPropertyUpdateKind.Dictionary)
            .ToList();
        Assert.Contains(containers, container => container.Count == 0);
        Assert.All(containers, container => Assert.Equal(SubjectPropertyUpdateMode.Complete, container.Mode));
        Assert.All(propertyUpdates.Where(propertyUpdate => propertyUpdate.Kind == SubjectPropertyUpdateKind.Object),
            reference => Assert.Equal(SubjectPropertyUpdateMode.Incremental, reference.Mode));
    }

    [Fact]
    public void WhenAnUpdateIsSerialized_ThenOnlyAModeOtherThanIncrementalIsWrittenByName()
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Father = new Person() };

        // Act
        var json = JsonSerializer.Serialize(SubjectUpdate.CreateCompleteUpdate(source, []));

        // Assert
        Assert.Contains("\"mode\":\"Complete\"", json);
        Assert.DoesNotContain(nameof(SubjectPropertyUpdateMode.Incremental), json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAContainerIsRebuiltWithTheSameMembersAndEveryMemberChanges_ThenTheDiffIsNotMarkedComplete(bool dictionary)
    {
        // Arrange
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var source = dictionary
            ? new Person(InterceptorSubjectContext.Create().WithRegistry()) { Relationships = new() { ["first"] = first, ["second"] = second } }
            : new Person(InterceptorSubjectContext.Create().WithRegistry()) { Children = [first, second] };
        var propertyName = dictionary ? nameof(Person.Relationships) : nameof(Person.Children);
        var property = source.TryGetRegisteredProperty(propertyName)!;
        var previous = property.GetValue();
        object rebuilt = dictionary
            ? new Dictionary<string, Person>(source.Relationships!)
            : new List<Person>(source.Children);
        property.SetValue(rebuilt);
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, timestamp, null, previous, rebuilt),
            SubjectPropertyChange.Create<string?>(new PropertyReference(first, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "First", "First changed"),
            SubjectPropertyChange.Create<string?>(new PropertyReference(second, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "Second", "Second changed")
        ];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert: the diff names every member once and has no operations, the shape that must not be read
        // as a whole membership.
        var propertyUpdate = update.Subjects[update.Root][propertyName];
        Assert.Null(propertyUpdate.Operations);
        Assert.Equal(propertyUpdate.Count, propertyUpdate.Items!.Count);
        Assert.Equal(SubjectPropertyUpdateMode.Incremental, propertyUpdate.Mode);
    }

    [Theory]
    [InlineData("different", SubjectPropertyUpdateMode.Replaced)]
    [InlineData("from-null", SubjectPropertyUpdateMode.Replaced)]
    [InlineData("assigned-back", SubjectPropertyUpdateMode.Incremental)]
    [InlineData("cleared", SubjectPropertyUpdateMode.Incremental)]
    public void WhenAReferenceChanges_ThenOnlyAChangeToAnotherSubjectIsMarkedReplaced(string scenario, SubjectPropertyUpdateMode expectedMode)
    {
        // Arrange
        var original = new Person { FirstName = "Original" };
        var replacement = new Person { FirstName = "Replacement" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var father = new PropertyReference(source, nameof(Person.Father));
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes = scenario switch
        {
            "different" =>
            [
                SubjectPropertyChange.Create<Person?>(father, ChangeOrigin.Local, timestamp, null, original, replacement)
            ],
            "from-null" =>
            [
                SubjectPropertyChange.Create<Person?>(father, ChangeOrigin.Local, timestamp, null, null, replacement)
            ],
            "assigned-back" =>
            [
                SubjectPropertyChange.Create<Person?>(father, ChangeOrigin.Local, timestamp, null, original, replacement),
                SubjectPropertyChange.Create<Person?>(father, ChangeOrigin.Local, timestamp, null, replacement, original)
            ],
            _ =>
            [
                SubjectPropertyChange.Create<Person?>(father, ChangeOrigin.Local, timestamp, null, original, null)
            ]
        };
        source.Father = changes[^1].GetNewValue<Person?>();

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert
        var reference = update.Subjects[update.Root][nameof(Person.Father)];
        Assert.Equal(SubjectPropertyUpdateKind.Object, reference.Kind);
        Assert.Equal(expectedMode, reference.Mode);
    }

    [Fact]
    public void WhenAChangeIsBelowAReference_ThenThePathEntryIsNotMarkedReplaced()
    {
        // Arrange
        var father = new Person { FirstName = "Father" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Father = father };
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<string?>(new PropertyReference(father, nameof(Person.FirstName)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Father", "Changed")
        ];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert
        var pathEntry = update.Subjects[update.Root][nameof(Person.Father)];
        Assert.NotNull(pathEntry.Id);
        Assert.Equal(SubjectPropertyUpdateMode.Incremental, pathEntry.Mode);
    }

    [Fact]
    public void WhenOneOfTwoReferencesToASharedSubjectIsReassigned_ThenTheMirrorKeepsTheSharedSubjectInTheOther()
    {
        // Arrange: the mirror binds both references to one instance, as the source holds one subject there.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var shared = new Person { FirstName = "Shared" };
        var source = new Person(context) { Father = shared, Mother = shared };
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var mirroredShared = mirror.Father;
        var changes = CaptureChanges(context, () => source.Mother = new Person { FirstName = "New" });

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        mirror.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(mirroredShared, mirror.Father);
        Assert.Equal("Shared", mirror.Father!.FirstName);
        Assert.Equal("New", mirror.Mother!.FirstName);
    }

    [Fact]
    public void WhenAReferenceSharingAListChildIsReassigned_ThenTheListChildIsLeftIntact()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var shared = new Person { FirstName = "Shared" };
        var source = new Person(context) { Father = shared, Children = [shared] };
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var mirroredChild = Assert.Single(mirror.Children);
        var changes = CaptureChanges(context, () => source.Father = new Person { FirstName = "New" });

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        mirror.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(mirroredChild, Assert.Single(mirror.Children));
        Assert.Equal("Shared", mirroredChild.FirstName);
        Assert.Equal("New", mirror.Father!.FirstName);
    }

    [Fact]
    public void WhenAReplacedReferenceNamesAnIdBoundEarlierInTheUpdate_ThenItResolvesToThatSubject()
    {
        // Arrange
        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Father = father, Mother = mother };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [nameof(Person.Father)] = new() { Kind = SubjectPropertyUpdateKind.Object, Id = "2" },
                    [nameof(Person.Mother)] = new()
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Mode = SubjectPropertyUpdateMode.Replaced,
                        Id = "2"
                    }
                },
                ["2"] = new()
                {
                    [nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Shared" }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(father, target.Father);
        Assert.Same(father, target.Mother);
        Assert.Equal("Shared", father.FirstName);
        Assert.Equal("Mother", mother.FirstName);
    }

    [Fact]
    public void WhenADictionaryIsRebuiltWithTheSameEntriesWhileAnotherSubjectMovesIn_ThenTheMirrorLosesNoMember()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var moved = new Person { FirstName = "Moved" };
        var source = new Person(context)
        {
            Father = moved,
            Relationships = new() { ["first"] = first, ["second"] = second }
        };
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var mirroredSecond = mirror.Relationships!["second"];

        // The batch rebuilds the dictionary with the same entries and changes two subjects. A later change
        // moves one of them into the dictionary before the batch is built, so the paths read from the live
        // graph name as many keys as the rebuilt dictionary counts.
        var changes = CaptureChanges(context, () =>
        {
            source.Relationships = new() { ["first"] = first, ["second"] = second };
            first.FirstName = "First changed";
            moved.FirstName = "Moved changed";
        });
        source.Father = null;
        source.Relationships = new() { ["first"] = first, ["second"] = second, ["moved"] = moved };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        mirror.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var relationships = update.Subjects[update.Root][nameof(Person.Relationships)];
        Assert.Equal(relationships.Count, relationships.Items!.Count);
        Assert.Same(mirroredSecond, mirror.Relationships!["second"]);
        Assert.Equal("First changed", mirror.Relationships["first"].FirstName);
    }

    [Fact]
    public void WhenABackReferenceIsReassignedToAnAncestorTheBatchCompletes_ThenThePreviousTargetIsLeftIntact()
    {
        // Arrange: assigning the ancestor completes it and, through its first parent, the changed subject, so
        // the reassigned reference reaches the update through a complete payload rather than through the change.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var previous = new CycleTestNode { Name = "Previous" };
        var child = new CycleTestNode { Name = "Child" };
        var ancestor = new CycleTestNode { Name = "Ancestor" };
        var source = new CycleTestNode(context) { Name = "Root", Items = [previous], Child = ancestor };
        ancestor.Child = child;
        child.Parent = previous;
        var mirror = CreateMirror(source);
        var changes = CaptureChanges(context, () => child.Parent = ancestor);

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Previous", mirror.Items[0].Name);
        Assert.Same(mirror.Child, mirror.Child!.Child!.Parent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAReferenceSharedWithAListItemIsReassignedWhileItsSubjectIsCompleted_ThenTheListItemIsLeftIntact(bool reassignmentFirst)
    {
        // Arrange: attaching the holder a second time completes it, so its reassigned reference reaches the
        // update through that complete payload, whichever change arrives first.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var shared = new CycleTestNode { Name = "Shared" };
        var holder = new CycleTestNode { Name = "Holder" };
        var source = new CycleTestNode(context) { Name = "Root", Items = [holder, shared] };
        holder.Child = shared;
        var mirror = CreateMirror(source);
        var changes = CaptureChanges(context, () =>
        {
            if (reassignmentFirst)
                holder.Child = new CycleTestNode { Name = "Fresh" };

            source.Child = holder;

            if (!reassignmentFirst)
                holder.Child = new CycleTestNode { Name = "Fresh" };
        });

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Shared", mirror.Items[1].Name);
        Assert.Equal("Fresh", mirror.Items[0].Child!.Name);
    }

    private static CycleTestNode CreateMirror(CycleTestNode source)
    {
        var mirror = new CycleTestNode(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        return mirror;
    }

    private static SubjectPropertyChange[] CaptureChanges(IInterceptorSubjectContext context, Action change)
    {
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            change();
        }

        return changes.ToArray();
    }
}
