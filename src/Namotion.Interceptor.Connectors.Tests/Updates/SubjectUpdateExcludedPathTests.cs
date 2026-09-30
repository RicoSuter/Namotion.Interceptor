using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that an <see cref="ISubjectUpdateProcessor"/> excluding a property also hides everything reached
/// only through it. A permissions-style processor relies on this: the excluded property name and the values
/// behind it must not appear, no matter which subject in the batch the change belongs to.
/// </summary>
public class SubjectUpdateExcludedPathTests
{
    [Theory]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.Relationships))]
    public void WhenAChangedSubjectIsReachableOnlyThroughAnExcludedProperty_ThenNeitherTheEdgeNorTheValueIsEmitted(string excludedName)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        var hidden = new Person { FirstName = "Hidden" };
        var visible = new Person { FirstName = "Visible" };
        object value = excludedName switch
        {
            nameof(Person.Children) => new List<Person> { hidden },
            nameof(Person.Relationships) => new Dictionary<string, Person> { ["hidden"] = hidden },
            _ => hidden
        };
        source.TryGetRegisteredProperty(excludedName)!.SetValue(value);
        source.Father = visible;
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(new PropertyReference(source, excludedName),
                ChangeOrigin.Local, timestamp, null, null, value),
            SubjectPropertyChange.Create<string?>(new PropertyReference(hidden, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "Hidden", "Secret"),
            SubjectPropertyChange.Create<string?>(new PropertyReference(visible, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "Visible", "Public")
        ];
        var processor = new PropertyNameExclusionProcessor(source, excludedName);

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, [processor]);

        // Assert
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(excludedName));
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.Values.Any(property => Equals(property.Value, "Secret")));
        Assert.False(update.Subjects.ContainsKey(((IInterceptorSubject)hidden).GetOrAddSubjectId()));
        var visibleProperties = Assert.Single(update.Subjects).Value;
        Assert.Equal("Public", visibleProperties[nameof(Person.FirstName)].Value);
    }

    [Fact]
    public void WhenASubjectBehindAnExcludedPropertyIsAlsoReferencedThroughAnIncludedOne_ThenItsChangeIsEmitted()
    {
        // Arrange: the included reference makes the subject reachable although its first parent is excluded
        var hidden = new Person { FirstName = "Hidden", LastName = "Old" };
        var visible = new Person { FirstName = "Visible" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root", Mother = hidden, Father = visible };
        visible.Father = hidden;
        hidden.LastName = "New";
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<Person?>(new PropertyReference(visible, nameof(Person.Father)),
                ChangeOrigin.Local, timestamp, null, null, hidden),
            SubjectPropertyChange.Create<string?>(new PropertyReference(hidden, nameof(Person.LastName)),
                ChangeOrigin.Local, timestamp, null, "Old", "New")
        ];
        var processor = new PropertyNameExclusionProcessor(source, nameof(Person.Mother));
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, [processor]), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, [processor]);
        mirror.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.False(update.Subjects.TryGetValue(update.Root!, out var rootProperties) &&
            rootProperties.ContainsKey(nameof(Person.Mother)));
        Assert.Null(mirror.Mother);
        Assert.Equal("New", mirror.Father!.Father!.LastName);
    }

    [Fact]
    public void WhenAValueChangesInsideAnExcludedSubtree_ThenTheUpdateIsEmpty()
    {
        // Arrange
        var source = new CycleTestNode(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { Name = "Root" };
        source.Child = new CycleTestNode { Name = "Secret", Items = [new CycleTestNode { Name = "Nested" }] };
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<string?>(new PropertyReference(source.Child, nameof(CycleTestNode.Name)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Secret", "Changed"),
            SubjectPropertyChange.Create<string?>(new PropertyReference(source.Child.Items[0], nameof(CycleTestNode.Name)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Nested", "Changed")
        ];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes,
            [new PropertyNameExclusionProcessor(source, nameof(CycleTestNode.Child))]);

        // Assert
        Assert.Empty(update.Subjects);
    }

    private sealed class PropertyNameExclusionProcessor(IInterceptorSubject owner, string propertyName) : ISubjectUpdateProcessor
    {
        public bool IsIncluded(RegisteredSubjectProperty property)
            => property.Name != propertyName || !ReferenceEquals(property.Parent.Subject, owner);
    }
}
