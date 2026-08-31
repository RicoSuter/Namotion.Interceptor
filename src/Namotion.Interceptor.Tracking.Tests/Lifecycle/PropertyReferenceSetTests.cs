using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Direct tests for the inline-optimized set used by <see cref="LifecycleInterceptor"/>,
/// pinning its documented invariants: empty sentinel is First.Subject == null, Additional
/// never contains First, and the backing HashSet is released when it drains.
/// </summary>
public class PropertyReferenceSetTests
{
    private static PropertyReference CreateReference(string propertyName) =>
        new(new Person { FirstName = "Test" }, propertyName);

    [Fact]
    public void WhenSetIsEmpty_ThenIsEmptyIsTrueAndRemoveReturnsFalse()
    {
        // Arrange
        var set = default(PropertyReferenceSet);

        // Act & Assert
        Assert.True(set.IsEmpty);
        Assert.False(set.Remove(CreateReference("FirstName")));
    }

    [Fact]
    public void WhenFirstReferenceIsAdded_ThenAddReturnsTrueAndSetIsNotEmpty()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var reference = CreateReference("FirstName");

        // Act
        var added = set.Add(reference);

        // Assert
        Assert.True(added);
        Assert.False(set.IsEmpty);
        Assert.Null(set.Additional);
    }

    [Fact]
    public void WhenSameReferenceIsAddedTwice_ThenSecondAddReturnsFalse()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var reference = CreateReference("FirstName");
        set.Add(reference);

        // Act
        var addedAgain = set.Add(reference);

        // Assert
        Assert.False(addedAgain);
        Assert.Null(set.Additional);
    }

    [Fact]
    public void WhenDuplicateOfAdditionalReferenceIsAdded_ThenAddReturnsFalse()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var subject = new Person { FirstName = "Test" };
        var first = new PropertyReference(subject, "FirstName");
        var second = new PropertyReference(subject, "LastName");
        set.Add(first);
        set.Add(second);

        // Act
        var addedAgain = set.Add(second);

        // Assert
        Assert.False(addedAgain);
    }

    [Fact]
    public void WhenOnlyReferenceIsRemoved_ThenSetIsEmpty()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var reference = CreateReference("FirstName");
        set.Add(reference);

        // Act
        var removed = set.Remove(reference);

        // Assert
        Assert.True(removed);
        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void WhenFirstIsRemovedWithAdditionalPresent_ThenPromotedReferenceIsRemovableExactlyOnce()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var subject = new Person { FirstName = "Test" };
        var first = new PropertyReference(subject, "FirstName");
        var second = new PropertyReference(subject, "LastName");
        set.Add(first);
        set.Add(second);

        // Act: removing First forces promotion of the only Additional element.
        var removed = set.Remove(first);

        // Assert: the promoted reference moved into the First slot and the drained
        // backing set was released; it is removable exactly once (a violation of the
        // "Additional never contains First" invariant would make it removable twice).
        Assert.True(removed);
        Assert.False(set.IsEmpty);
        Assert.True(set.First.Equals(second));
        Assert.Null(set.Additional);
        Assert.True(set.Remove(second));
        Assert.False(set.Remove(second));
        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void WhenFirstIsRemovedWithMultipleAdditional_ThenAllReferencesRemainRemovable()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var subject = new Person { FirstName = "Test" };
        var first = new PropertyReference(subject, "FirstName");
        var second = new PropertyReference(subject, "LastName");
        var third = new PropertyReference(subject, "Father");
        set.Add(first);
        set.Add(second);
        set.Add(third);

        // Act
        var removed = set.Remove(first);

        // Assert: promotion picks an arbitrary survivor; both survivors must remain
        // removable exactly once and the set must drain to empty.
        Assert.True(removed);
        Assert.True(set.Remove(second));
        Assert.True(set.Remove(third));
        Assert.True(set.IsEmpty);
        Assert.False(set.Remove(second));
        Assert.False(set.Remove(third));
    }

    [Fact]
    public void WhenAdditionalReferenceIsRemoved_ThenDrainedBackingSetIsReleased()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var subject = new Person { FirstName = "Test" };
        var first = new PropertyReference(subject, "FirstName");
        var second = new PropertyReference(subject, "LastName");
        set.Add(first);
        set.Add(second);

        // Act
        var removed = set.Remove(second);

        // Assert
        Assert.True(removed);
        Assert.Null(set.Additional);
        Assert.True(set.First.Equals(first));
        Assert.False(set.IsEmpty);
    }

    [Fact]
    public void WhenUnknownReferenceIsRemovedFromNonEmptySet_ThenRemoveReturnsFalse()
    {
        // Arrange
        var set = default(PropertyReferenceSet);
        var subject = new Person { FirstName = "Test" };
        set.Add(new PropertyReference(subject, "FirstName"));

        // Act & Assert
        Assert.False(set.Remove(new PropertyReference(subject, "LastName")));
        Assert.False(set.IsEmpty);
    }
}
