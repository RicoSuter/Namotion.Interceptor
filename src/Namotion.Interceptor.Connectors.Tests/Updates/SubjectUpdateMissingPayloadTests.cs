using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateMissingPayloadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenAnItemReferencesAMissingSubject_ThenApplyReportsFailureAndPreservesTheCollection(bool dictionary, bool insert)
    {
        // Arrange
        var child = new Person { FirstName = "Old" };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [child],
            Relationships = new Dictionary<string, Person> { ["child"] = child }
        };
        var originalChildren = target.Children;
        var originalRelationships = target.Relationships;
        object index = dictionary ? "child" : 0;
        var propertyUpdate = new SubjectPropertyUpdate
        {
            Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
            Count = 1,
            Operations = insert ? [new SubjectCollectionOperation { Action = SubjectCollectionOperationType.Insert, Index = index, Id = "missing" }] : null,
            Items = insert ? null : [new SubjectPropertyItemUpdate { Index = index, Id = "missing" }]
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [dictionary ? "Relationships" : "Children"] = propertyUpdate,
                    ["FirstName"] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Updated" }
                }
            }
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Contains("missing", exception.Message);
        Assert.Same(originalChildren, target.Children);
        Assert.Same(originalRelationships, target.Relationships);
        Assert.Same(child, Assert.Single(target.Children));
        Assert.Same(child, target.Relationships!["child"]);
        Assert.Equal("Old", child.FirstName);
        Assert.Equal("Updated", target.FirstName);
    }
}
