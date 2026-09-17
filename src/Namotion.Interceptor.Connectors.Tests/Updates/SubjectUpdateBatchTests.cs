using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAChildChangePrecedesAMergedObjectAssignment_ThenTheCompleteChildAndChangeTimestampArrive(bool attributeOnly)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var parent = new Person { LastName = "Ancestor" };
        source.Mother = parent;
        var child = new Person { FirstName = "Initial", LastName = "Retained", Mother = parent, Father = source };
        parent.Father = child;
        child.FirstName = attributeOnly ? "Initial" : "Later";
        child.FirstName_MaxLength = attributeOnly ? 789 : 456;
        parent.Father = null;
        parent.Father = child;
        var reference = new PropertyReference(parent, nameof(Person.Father));
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<Person?>(reference, ChangeOrigin.Local, timestamp.AddSeconds(-2), null, null, child),
            attributeOnly
                ? SubjectPropertyChange.Create(new PropertyReference(child, nameof(Person.FirstName_MaxLength)),
                    ChangeOrigin.Local, timestamp.AddSeconds(-1), null, 123, 456)
                : SubjectPropertyChange.Create<string?>(new PropertyReference(child, nameof(Person.FirstName)),
                    ChangeOrigin.Local, timestamp.AddSeconds(-1), null, "Initial", "Final"),
            SubjectPropertyChange.Create<Person?>(reference, ChangeOrigin.Local, timestamp, null, child, null),
            SubjectPropertyChange.Create<Person?>(reference, ChangeOrigin.Local, timestamp, null, null, child)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var receivedChild = target.Mother!.Father!;
        Assert.Equal(attributeOnly ? "Initial" : "Final", receivedChild.FirstName);
        Assert.Equal("Retained", receivedChild.LastName);
        Assert.Equal(456, receivedChild.FirstName_MaxLength);
        Assert.Equal("Count", receivedChild.FirstName_MaxLength_Unit);
        var parentUpdate = Assert.Single(update.Subjects[update.Root]).Value;
        var referenceUpdate = Assert.Single(update.Subjects[parentUpdate.Id!]).Value;
        Assert.Equal(timestamp, referenceUpdate.Timestamp);
        var childProperties = update.Subjects[referenceUpdate.Id!];
        Assert.Equal(update.Root, childProperties[nameof(Person.Father)].Id);
        Assert.Equal(parentUpdate.Id, childProperties[nameof(Person.Mother)].Id);
        var changedProperty = childProperties[nameof(Person.FirstName)];
        Assert.Equal(timestamp.AddSeconds(-1), attributeOnly
            ? changedProperty.Attributes!["MaxLength"].Timestamp
            : changedProperty.Timestamp);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenTwoCollectionAssignmentsArePassedAsOneBatch_ThenTheCompleteFinalCollectionArrives(bool dictionary, bool reverseArrival)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        object empty = dictionary ? new Dictionary<string, Person>() : new List<Person>();
        object intermediate = dictionary ? new Dictionary<string, Person> { ["A"] = first } : new List<Person> { first };
        object final = dictionary ? new Dictionary<string, Person> { ["A"] = first, ["B"] = second } : new List<Person> { first, second };
        var property = source.TryGetRegisteredProperty(dictionary ? nameof(Person.Relationships) : nameof(Person.Children))!;
        property.SetValue(intermediate);
        property.SetValue(final);
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, timestamp.AddSeconds(-1), null, empty, intermediate, revision: 1),
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, timestamp, null, intermediate, final, revision: 2)
        ];
        if (reverseArrival) Array.Reverse(changes);
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Relationships = new() };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(timestamp, update.Subjects[update.Root][property.Name].Timestamp);
        Assert.Equal(2, dictionary ? target.Relationships!.Count : target.Children.Count);
        Assert.Equal("A", dictionary ? target.Relationships!["A"].FirstName : target.Children[0].FirstName);
        Assert.Equal("B", dictionary ? target.Relationships!["B"].FirstName : target.Children[1].FirstName);
    }

    [Fact]
    public void WhenANewSubjectChangesWhileAttaching_ThenItsCompletePayloadArrives()
    {
        // Arrange
        var context = CreateContextWritingOnAttach(person =>
        {
            if (person.FirstName == "NewFather")
                person.LastName = "Initialized";
        });
        var mother = new Person { FirstName = "Mother" };
        var source = new Person(context) { FirstName = "Root", Mother = mother };
        var changes = CaptureChanges(context);

        // Act
        var newFather = new Person { FirstName = "NewFather" };
        mother.Father = newFather;
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);

        // Assert
        var newSubjectChangeIndex = changes.FindIndex(change => ReferenceEquals(change.Property.Subject, newFather));
        var referenceChangeIndex = changes.FindIndex(change =>
            ReferenceEquals(change.Property.Subject, mother) && change.Property.Name == nameof(Person.Father));
        Assert.True(newSubjectChangeIndex >= 0 && newSubjectChangeIndex < referenceChangeIndex,
            "the new subject must change before the reference change which introduces it");

        var motherId = update.Subjects[update.Root][nameof(Person.Mother)].Id!;
        var newFatherProperties = update.Subjects[update.Subjects[motherId][nameof(Person.Father)].Id!];
        var firstName = newFatherProperties[nameof(Person.FirstName)];
        Assert.Equal("NewFather", firstName.Value);
        Assert.Equal(123, firstName.Attributes!["MaxLength"].Value);
        Assert.Equal("Initialized", newFatherProperties[nameof(Person.LastName)].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnInsertedItemsChildChangesWhileAttaching_ThenTheCompleteChildArrives(bool dictionary)
    {
        // Arrange
        var context = CreateContextWritingOnAttach(person =>
        {
            if (person.FirstName == "Grandchild" && person.LastName is null)
                person.LastName = "Initialized";
        });
        var source = new Person(context) { FirstName = "Root", Relationships = new Dictionary<string, Person>() };
        var target = CreateMirror(source);
        var changes = CaptureChanges(context);
        var child = new Person { FirstName = "Child", Father = new Person { FirstName = "Grandchild" } };

        // Act
        if (dictionary)
            source.Relationships = new Dictionary<string, Person> { ["child"] = child };
        else
            source.Children = [child];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var receivedChild = dictionary ? target.Relationships!["child"] : Assert.Single(target.Children);
        Assert.Equal("Child", receivedChild.FirstName);
        Assert.Equal("Grandchild", receivedChild.Father!.FirstName);
        Assert.Equal("Initialized", receivedChild.Father.LastName);
    }

    [Fact]
    public void WhenANestedNewSubjectAssignsAReferenceWhileAttaching_ThenTheCompleteNestedSubjectArrives()
    {
        // Arrange
        var context = CreateContextWritingOnAttach(person =>
        {
            if (person.FirstName == "Grandchild" && person.Father is null)
                person.Father = new Person { FirstName = "GreatGrandchild" };
        });
        var source = new Person(context) { FirstName = "Root" };
        var target = CreateMirror(source);
        var changes = CaptureChanges(context);

        // Act
        source.Father = new Person { FirstName = "Child", Mother = new Person { FirstName = "Grandchild", LastName = "Retained" } };
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var receivedGrandchild = target.Father!.Mother!;
        Assert.Equal("Grandchild", receivedGrandchild.FirstName);
        Assert.Equal("Retained", receivedGrandchild.LastName);
        Assert.Equal("GreatGrandchild", receivedGrandchild.Father!.FirstName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenAnInsertedItemChangesInTheSameBatch_ThenItsCapturedValueAndTimestampArrive(bool dictionary, bool itemChangeFirst)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Relationships = new Dictionary<string, Person>() };
        var oldChildren = source.Children;
        var oldRelationships = source.Relationships;
        var child = new Person { FirstName = "Initial", LastName = "Retained" };
        if (dictionary)
            source.Relationships = new Dictionary<string, Person> { ["child"] = child };
        else
            source.Children = [child];
        child.FirstName = "Later";

        var timestamp = DateTimeOffset.UtcNow;
        var itemChange = SubjectPropertyChange.Create<string?>(new PropertyReference(child, nameof(Person.FirstName)),
            ChangeOrigin.Local, timestamp.AddSeconds(-1), null, "Initial", "Final");
        var containerChange = dictionary
            ? SubjectPropertyChange.Create<Dictionary<string, Person>?>(new PropertyReference(source, nameof(Person.Relationships)),
                ChangeOrigin.Local, timestamp, null, oldRelationships, source.Relationships)
            : SubjectPropertyChange.Create<List<Person>>(new PropertyReference(source, nameof(Person.Children)),
                ChangeOrigin.Local, timestamp, null, oldChildren, source.Children);
        SubjectPropertyChange[] changes = itemChangeFirst ? [itemChange, containerChange] : [containerChange, itemChange];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert
        var containerUpdate = update.Subjects[update.Root][dictionary ? nameof(Person.Relationships) : nameof(Person.Children)];
        var childProperties = update.Subjects[Assert.Single(containerUpdate.Operations!).Id!];
        Assert.Null(containerUpdate.Items);
        Assert.Equal("Final", childProperties[nameof(Person.FirstName)].Value);
        Assert.Equal(timestamp.AddSeconds(-1), childProperties[nameof(Person.FirstName)].Timestamp);
        Assert.Equal("Retained", childProperties[nameof(Person.LastName)].Value);
    }

    [Fact]
    public void WhenAnExistingSubjectIsAssignedToASecondReference_ThenItsCompletePayloadArrives()
    {
        // Arrange
        var mother = new Person { FirstName = "Mother", LastName = "Shared" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root", Mother = mother };
        var oldChildren = mother.Children;
        mother.Children = [new Person { FirstName = "Inserted" }];
        source.Father = mother;

        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<List<Person>>(new PropertyReference(mother, nameof(Person.Children)),
                ChangeOrigin.Local, timestamp, null, oldChildren, mother.Children),
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Father)),
                ChangeOrigin.Local, timestamp, null, null, mother)
        ];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert: on the update, because the C# applier applies a payload at the first reference to its id only
        var fatherProperties = update.Subjects[update.Subjects[update.Root][nameof(Person.Father)].Id!];
        Assert.Equal("Shared", fatherProperties[nameof(Person.LastName)].Value);
        Assert.Equal(1, fatherProperties[nameof(Person.Children)].Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnAssignedSubjectsCollectionAlsoChangesInTheSameBatch_ThenItsFinalMembershipArrives(bool assignmentFirst)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        var target = CreateMirror(source);
        var firstChild = new Person { FirstName = "First" };
        var secondChild = new Person { FirstName = "Second" };
        var assigned = new Person { FirstName = "Assigned", Children = [firstChild] };
        var oldChildren = assigned.Children;
        source.Mother = assigned;
        assigned.Children = [firstChild, secondChild];

        var timestamp = DateTimeOffset.UtcNow;
        var assignment = SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Mother)),
            ChangeOrigin.Local, timestamp, null, null, assigned);
        var childrenChange = SubjectPropertyChange.Create<List<Person>>(new PropertyReference(assigned, nameof(Person.Children)),
            ChangeOrigin.Local, timestamp, null, oldChildren, assigned.Children);
        SubjectPropertyChange[] changes = assignmentFirst ? [assignment, childrenChange] : [childrenChange, assignment];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var childrenUpdate = update.Subjects[update.Subjects[update.Root][nameof(Person.Mother)].Id!][nameof(Person.Children)];
        Assert.Null(childrenUpdate.Operations);
        Assert.Equal(2, childrenUpdate.Count);
        Assert.Equal(new[] { "First", "Second" }, target.Mother!.Children.Select(child => child.FirstName));
    }

    private static IInterceptorSubjectContext CreateContextWritingOnAttach(Action<Person> write)
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => new WriteOnAttachInitializer(write));
    }

    private static List<SubjectPropertyChange> CaptureChanges(IInterceptorSubjectContext context)
    {
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        return changes;
    }

    private static Person CreateMirror(Person source)
    {
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        return target;
    }

    /// <summary>
    /// Writes to a subject while it attaches, so that its own change is published before the reference
    /// change which introduced it.
    /// </summary>
    private sealed class WriteOnAttachInitializer(Action<Person> write) : ISubjectPropertyInitializer
    {
        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.IsAttribute || property.Name != nameof(Person.FirstName))
                return;

            write((Person)property.Subject);
        }
    }
}
