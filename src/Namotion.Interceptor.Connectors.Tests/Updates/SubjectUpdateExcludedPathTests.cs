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
    [Fact]
    public void WhenAChangesPathToRootCrossesAnExcludedReference_ThenNeitherTheEdgeNorTheValueIsEmitted()
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        var hidden = new Person { FirstName = "Hidden" };
        var visible = new Person { FirstName = "Visible" };
        source.Mother = hidden;
        source.Father = visible;
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Mother)),
                ChangeOrigin.Local, timestamp, null, null, hidden),
            SubjectPropertyChange.Create<string?>(new PropertyReference(hidden, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "Hidden", "Secret"),
            SubjectPropertyChange.Create<string?>(new PropertyReference(visible, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, "Visible", "Public")
        ];
        var processor = new PropertyNameExclusionProcessor(source, nameof(Person.Mother));

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, [processor]);

        // Assert
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(nameof(Person.Mother)));
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.Values.Any(property => Equals(property.Value, "Secret")));
        Assert.Equal(2, update.Subjects.Count);
        var visibleId = update.Subjects[update.Root][nameof(Person.Father)].Id;
        Assert.NotNull(visibleId);
        Assert.Equal("Public", update.Subjects[visibleId][nameof(Person.FirstName)].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAChangesPathToRootCrossesAnExcludedContainer_ThenNeitherTheEdgeNorTheValueIsEmitted(bool dictionary)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        var hidden = new Person { FirstName = "Hidden" };
        var visible = new Person { FirstName = "Visible" };
        var excludedName = dictionary ? nameof(Person.Relationships) : nameof(Person.Children);
        object container = dictionary
            ? new Dictionary<string, Person> { ["hidden"] = hidden }
            : new List<Person> { hidden };
        source.TryGetRegisteredProperty(excludedName)!.SetValue(container);
        source.Father = visible;
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(new PropertyReference(source, excludedName),
                ChangeOrigin.Local, timestamp, null, null, container),
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

    private sealed class PropertyNameExclusionProcessor(IInterceptorSubject owner, string propertyName) : ISubjectUpdateProcessor
    {
        public bool IsIncluded(RegisteredSubjectProperty property)
            => property.Name != propertyName || !ReferenceEquals(property.Parent.Subject, owner);
    }
}
