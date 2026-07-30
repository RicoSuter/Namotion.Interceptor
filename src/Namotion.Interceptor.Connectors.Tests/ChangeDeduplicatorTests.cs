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

    [Fact]
    public void WhenTheNewerCommitArrivesFirst_ThenTheSurvivorTakesItsNewValue()
    {
        // Arrange - the enqueue order is inverted against the commit order, which is the race the
        // revision fixes: enqueuing happens after the commit and outside the subject lock.
        using var deduplicator = new ChangeDeduplicator();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "NewerOld", "NewerNew", revision: 20),
            CreateChange(property, "OlderOld", "OlderNew", revision: 10)
        ];

        // Act
        var deduplicated = deduplicator.Deduplicate(changes).ToArray();

        // Assert
        var change = Assert.Single(deduplicated);
        Assert.Equal("OlderOld", change.GetOldValue<string>());
        Assert.Equal("NewerNew", change.GetNewValue<string>());
        Assert.Equal(20, change.Revision);
    }

    [Fact]
    public void WhenThreeCommitsArriveOutOfOrder_ThenTheSurvivorSpansTheLowestAndHighestRevision()
    {
        // Arrange
        using var deduplicator = new ChangeDeduplicator();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "Old14", "New14", revision: 14),
            CreateChange(property, "Old21", "New21", revision: 21),
            CreateChange(property, "Old7", "New7", revision: 7)
        ];

        // Act
        var deduplicated = deduplicator.Deduplicate(changes).ToArray();

        // Assert - the baseline comes from the lowest revision, the current state from the highest
        var change = Assert.Single(deduplicated);
        Assert.Equal("Old7", change.GetOldValue<string>());
        Assert.Equal("New21", change.GetNewValue<string>());
        Assert.Equal(21, change.Revision);
    }

    [Fact]
    public void WhenChangesBelongToDifferentSubjects_ThenEachPropertyIsCollapsedIndependently()
    {
        // Arrange - revisions of different subjects are not comparable, so the two properties must be
        // collapsed against their own revisions only.
        using var deduplicator = new ChangeDeduplicator();

        var firstSubject = new Person();
        var secondSubject = new Person();

        var firstProperty = new PropertyReference(firstSubject, nameof(Person.FirstName));
        var secondProperty = new PropertyReference(secondSubject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(firstProperty, "FirstOld12", "FirstNew12", revision: 12),
            CreateChange(secondProperty, "SecondOld3", "SecondNew3", revision: 3),
            CreateChange(firstProperty, "FirstOld5", "FirstNew5", revision: 5),
            CreateChange(secondProperty, "SecondOld8", "SecondNew8", revision: 8)
        ];

        // Act
        var deduplicated = deduplicator.Deduplicate(changes).ToArray();

        // Assert - both survive, in the arrival order of their last occurrence
        Assert.Equal(2, deduplicated.Length);

        Assert.Equal(firstProperty, deduplicated[0].Property);
        Assert.Equal("FirstOld5", deduplicated[0].GetOldValue<string>());
        Assert.Equal("FirstNew12", deduplicated[0].GetNewValue<string>());

        Assert.Equal(secondProperty, deduplicated[1].Property);
        Assert.Equal("SecondOld3", deduplicated[1].GetOldValue<string>());
        Assert.Equal("SecondNew8", deduplicated[1].GetNewValue<string>());
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
