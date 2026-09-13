using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
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
}
