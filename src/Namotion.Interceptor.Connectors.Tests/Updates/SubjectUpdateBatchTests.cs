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
        var target = CreateMirror(source);
        // The batch below also clears and restores the reference; the source only needs to end up holding it,
        // since clearing it for real would take the root out of the graph with the child's back reference.
        var child = new Person { FirstName = "Initial", LastName = "Retained", Mother = parent, Father = source };
        parent.Father = child;
        child.FirstName = attributeOnly ? "Initial" : "Later";
        child.FirstName_MaxLength = attributeOnly ? 789 : 456;
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

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var receivedChild = target.Mother!.Father!;
        Assert.Equal(attributeOnly ? "Initial" : "Final", receivedChild.FirstName);
        Assert.Equal("Retained", receivedChild.LastName);
        Assert.Equal(456, receivedChild.FirstName_MaxLength);
        Assert.Equal("Count", receivedChild.FirstName_MaxLength_Unit);
        var referenceUpdate = update.Subjects[GetId(parent)][nameof(Person.Father)];
        Assert.Equal(timestamp, referenceUpdate.Timestamp);
        var childProperties = update.Subjects[referenceUpdate.Id!];
        Assert.Equal(update.Root, childProperties[nameof(Person.Father)].Id);
        Assert.Equal(GetId(parent), childProperties[nameof(Person.Mother)].Id);
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
        Assert.Equal(timestamp, update.Subjects[update.Root!][property.Name].Timestamp);
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

        var newFatherProperties = update.Subjects[update.Subjects[GetId(mother)][nameof(Person.Father)].Id!];
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
        var containerUpdate = update.Subjects[update.Root!][dictionary ? nameof(Person.Relationships) : nameof(Person.Children)];
        var childProperties = update.Subjects[Assert.Single(containerUpdate.Items!).Id];
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

        // Assert: on the update, since the rule is about what the producer states
        var fatherProperties = update.Subjects[update.Subjects[update.Root!][nameof(Person.Father)].Id!];
        Assert.Equal("Shared", fatherProperties[nameof(Person.LastName)].Value);
        Assert.Single(fatherProperties[nameof(Person.Children)].Items!);
    }

    [Theory]
    [InlineData(nameof(Person.LastName), false)]
    [InlineData(nameof(Person.LastName), true)]
    [InlineData(nameof(Person.Children), false)]
    [InlineData(nameof(Person.Children), true)]
    public void WhenASubjectAssignedToASecondReferenceAlsoChanges_ThenItsCompletePayloadCarriesTheChange(string changedPropertyName, bool assignmentFirst)
    {
        // Arrange
        var mother = new Person { FirstName = "Mother", LastName = "Old" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root", Mother = mother };
        var oldChildren = mother.Children;
        mother.LastName = "New";
        mother.Children = [new Person { FirstName = "Inserted" }];
        source.Father = mother;

        var timestamp = DateTimeOffset.UtcNow;
        var memberChange = changedPropertyName == nameof(Person.Children)
            ? SubjectPropertyChange.Create<List<Person>>(new PropertyReference(mother, nameof(Person.Children)),
                ChangeOrigin.Local, timestamp, null, oldChildren, mother.Children)
            : SubjectPropertyChange.Create<string?>(new PropertyReference(mother, nameof(Person.LastName)),
                ChangeOrigin.Local, timestamp, null, "Old", "New");
        var assignment = SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Father)),
            ChangeOrigin.Local, timestamp, null, null, mother);
        SubjectPropertyChange[] changes = assignmentFirst ? [assignment, memberChange] : [memberChange, assignment];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert: on the update, since the rule is about what the producer states
        var motherId = GetId(mother);
        Assert.Equal(motherId, update.Subjects[update.Root!][nameof(Person.Father)].Id);
        Assert.Contains(motherId, update.CompleteSubjectIds!);
        Assert.Equal("New", update.Subjects[motherId][nameof(Person.LastName)].Value);
        Assert.Single(update.Subjects[motherId][nameof(Person.Children)].Items!);
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
        var childrenUpdate = update.Subjects[GetId(assigned)][nameof(Person.Children)];
        Assert.Equal(2, childrenUpdate.Items!.Count);
        Assert.All(childrenUpdate.Items, item => Assert.Contains(item.Id, update.CompleteSubjectIds!));
        Assert.Equal(new[] { "First", "Second" }, target.Mother!.Children.Select(child => child.FirstName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnAssignedSubjectsSubjectHoldingAttributeAlsoChangesInTheSameBatch_ThenItsCompleteValueArrives(bool assignmentFirst)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { FirstName = "Root" };
        var firstFriend = new Person { FirstName = "First" };
        var secondFriend = new Person { FirstName = "Second" };
        var assigned = new Person { FirstName = "Assigned" };
        List<Person> friends = [firstFriend];
        source.Father = assigned;
        var attribute = assigned.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!
            .AddAttribute("Friends", typeof(List<Person>), _ => friends, (_, value) => friends = (List<Person>)value!);
        var oldFriends = friends;
        attribute.SetValue(new List<Person> { firstFriend, secondFriend });

        var timestamp = DateTimeOffset.UtcNow;
        var assignment = SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Father)),
            ChangeOrigin.Local, timestamp, null, null, assigned);
        var attributeChange = SubjectPropertyChange.Create<object?>(attribute.Reference,
            ChangeOrigin.Local, timestamp, null, oldFriends, friends);
        SubjectPropertyChange[] changes = assignmentFirst ? [assignment, attributeChange] : [attributeChange, assignment];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert
        var assignedProperties = update.Subjects[update.Subjects[update.Root!][nameof(Person.Father)].Id!];
        var friendsUpdate = assignedProperties[nameof(Person.FirstName)].Attributes!["Friends"];
        Assert.Equal(2, friendsUpdate.Items!.Count);
        Assert.All(friendsUpdate.Items, item => Assert.Contains(item.Id, update.CompleteSubjectIds!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenAContainerGainsAMemberAndItsOwnerIsAssignedInOneBatch_ThenAFreshReceiverGetsEveryMember(bool dictionary, bool assignmentFirst)
    {
        // Arrange: the container change lists the retained member as known, which a receiver that never held its
        // owner does not, so the owner's complete payload has to carry that member as well.
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        var retained = new Person { FirstName = "A" };
        var added = new Person { FirstName = "B" };
        var parent = dictionary
            ? new Person { FirstName = "Parent", Relationships = new() { ["a"] = retained } }
            : new Person { FirstName = "Parent", Children = [retained] };
        object previousMembers = dictionary ? parent.Relationships! : parent.Children;
        object members = dictionary
            ? new Dictionary<string, Person> { ["a"] = retained, ["b"] = added }
            : new List<Person> { retained, added };
        source.Father = parent;
        parent.TryGetRegisteredProperty(dictionary ? nameof(Person.Relationships) : nameof(Person.Children))!.SetValue(members);

        var timestamp = DateTimeOffset.UtcNow;
        var containerChange = SubjectPropertyChange.Create<object?>(
            new PropertyReference(parent, dictionary ? nameof(Person.Relationships) : nameof(Person.Children)),
            ChangeOrigin.Local, timestamp, null, previousMembers, members);
        var assignment = SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Father)),
            ChangeOrigin.Local, timestamp, null, null, parent);
        SubjectPropertyChange[] changes = assignmentFirst ? [assignment, containerChange] : [containerChange, assignment];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var receivedMembers = dictionary
            ? target.Father!.Relationships!.OrderBy(entry => entry.Key).Select(entry => entry.Value)
            : target.Father!.Children;
        Assert.Equal(["A", "B"], receivedMembers.Select(member => member.FirstName));
    }

    private static IInterceptorSubjectContext CreateContextWritingOnAttach(Action<Person> write)
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => new WriteOnAttachInitializer(write));
    }

    [Fact]
    public void WhenARetainedChildChangesBeforeItsCollectionGainsAMember_ThenTheChildsChangeReachesTheMirror()
    {
        // Arrange: the collection diff has to name the retained child the child's own change already wrote.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var retained = new Person { FirstName = "Retained" };
        var source = new Person(context) { FirstName = "Root", Children = [retained] };
        var mirror = CreateMirror(source);
        var changes = CaptureChanges(context);
        retained.Mother = new Person { FirstName = "Added mother" };
        source.Children = [retained, new Person { FirstName = "Added child" }];

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Added mother", mirror.Children[0].Mother?.FirstName);
        Assert.Equal("Added child", mirror.Children[1].FirstName);
    }

    private static List<SubjectPropertyChange> CaptureChanges(IInterceptorSubjectContext context)
    {
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        return changes;
    }

    private static string GetId(IInterceptorSubject subject) => subject.TryGetSubjectId()!;

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
