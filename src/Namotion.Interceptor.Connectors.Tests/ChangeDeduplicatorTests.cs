using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeDeduplicatorTests
{
    [Fact]
    public void WhenTwoChangesToOnePropertyHaveNoRevision_ThenTheyCollapseByArrivalPosition()
    {
        // Arrange
        using var deduplicator = new ChangeDeduplicator();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "Value1", "Value2", revision: 0),
            CreateChange(property, "Value2", "Value3", revision: 0)
        ];

        // Act
        var deduplicated = deduplicator.Deduplicate(changes).ToArray();

        // Assert - the batch collapses to one change keeping the oldest old value and the newest new value
        var change = Assert.Single(deduplicated);
        Assert.Equal("Value1", change.GetOldValue<string>());
        Assert.Equal("Value3", change.GetNewValue<string>());
    }

    private static SubjectPropertyChange CreateChange(
        PropertyReference property,
        string? oldValue,
        string? newValue,
        long revision)
    {
        return SubjectPropertyChange.Create(
            property,
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue,
            revision);
    }
}
