using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that an <see cref="ISubjectUpdateProcessor"/> excluding a property also hides everything
/// reached through it. A permissions-style processor relies on this: the excluded property name and the
/// values behind it must not appear, no matter which subject in the batch the change belongs to.
/// </summary>
public class SubjectUpdateExcludedPathTests
{
    [Theory]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.Relationships))]
    public void WhenAChangesPathToRootCrossesAnExcludedProperty_ThenNeitherTheEdgeNorTheValueIsEmitted(string excludedName)
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
        Assert.Equal(2, update.Subjects.Count);
        var visibleId = update.Subjects[update.Root][nameof(Person.Father)].Id;
        Assert.NotNull(visibleId);
        Assert.Equal("Public", update.Subjects[visibleId][nameof(Person.FirstName)].Value);
    }

    [Fact]
    public void WhenASubjectCompletedThroughAnIncludedReferenceHasAnExcludedFirstParent_ThenItsChangeDoesNotStateTheExcludedProperty()
    {
        // Arrange: the included reference completes the subject, and the change inside it would otherwise
        // walk to the root through the excluded property that is its first parent.
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

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, [processor]);

        // Assert: only the root's Mother is excluded; the hidden subject's own payload still carries its Mother.
        Assert.False(update.Subjects[update.Root].ContainsKey(nameof(Person.Mother)));
    }

    private sealed class PropertyNameExclusionProcessor(IInterceptorSubject owner, string propertyName) : ISubjectUpdateProcessor
    {
        public bool IsIncluded(RegisteredSubjectProperty property)
            => property.Name != propertyName || !ReferenceEquals(property.Parent.Subject, owner);
    }
}
