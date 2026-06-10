using System.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectPropertyChangeOperationsTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
    }

    private static SubjectPropertyChange CreateChange(IInterceptorSubject subject, string propertyName, string? newValue)
    {
        var property = new PropertyReference(subject, propertyName);
        var oldValue = property.Metadata.GetValue?.Invoke(subject) as string;
        return SubjectPropertyChange.Create(
            property,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldValue,
            newValue: newValue);
    }

    [Fact]
    public void WhenApplyLocalChangesWithoutExclude_ThenAllChangesAreApplied()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var car = new Car(context);

        SubjectPropertyChange[] changes =
        [
            CreateChange(person, nameof(Person.FirstName), "John"),
            CreateChange(person, nameof(Person.LastName), "Doe"),
            CreateChange(car, nameof(Car.Name), "Tesla")
        ];

        // Act
        var (successful, failed, errors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude: null);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
        Assert.Equal("Tesla", car.Name);
        // On full success with no exclusions the Successful list is returned empty (zero-alloc);
        // the caller already holds the input span. Full success is detected via Failed.Count == 0.
        Assert.Empty(successful);
        Assert.Empty(failed);
        Assert.Empty(errors);
    }

    [Fact]
    public void WhenApplyLocalChangesWithExclude_ThenExcludedChangesAreSkipped()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var car = new Car(context);

        var first = CreateChange(person, nameof(Person.FirstName), "John");
        var second = CreateChange(person, nameof(Person.LastName), "Doe");
        var third = CreateChange(car, nameof(Car.Name), "Tesla");

        SubjectPropertyChange[] changes = [first, second, third];
        var exclude = new List<SubjectPropertyChange> { second };

        // Act
        var (successful, failed, errors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Equal("Tesla", car.Name);
        Assert.Equal(2, successful.Count);
        Assert.Empty(failed);
        Assert.Empty(errors);
    }

    [Fact]
    public void WhenExcludeIsOutOfOrder_ThenExcludedChangesAreStillSkipped()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var car = new Car(context);

        var first = CreateChange(person, nameof(Person.FirstName), "John");
        var second = CreateChange(person, nameof(Person.LastName), "Doe");
        var third = CreateChange(car, nameof(Car.Name), "Tesla");

        SubjectPropertyChange[] changes = [first, second, third];

        // Exclude is not an in-order subsequence of the span (third before first).
        var exclude = new List<SubjectPropertyChange> { third, first };

        // Act
        var (successful, failed, errors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude);

        // Assert
        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
        Assert.Equal(string.Empty, car.Name);
        Assert.Single(successful);
        Assert.Empty(failed);
        Assert.Empty(errors);
    }

    [Fact]
    public void WhenApplyFails_ThenFailedChangesAndErrorsAreReported()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var throwing = new ThrowingSetterPerson(context);
        var car = new Car(context);

        var first = CreateChange(person, nameof(Person.FirstName), "John");
        var failing = CreateChange(throwing, nameof(ThrowingSetterPerson.Name), "Boom");
        var third = CreateChange(car, nameof(Car.Name), "Tesla");

        SubjectPropertyChange[] changes = [first, failing, third];

        // Act
        var (successful, failed, errors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude: null);

        // Assert
        Assert.Contains(failing, failed);
        Assert.NotEmpty(errors);
        Assert.Contains(first, successful);
        Assert.Contains(third, successful);
        Assert.DoesNotContain(failing, successful);
    }

    [Fact]
    public void WhenExcludeAndApplyFailureCombine_ThenChangesArePartitionedCorrectly()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var throwing = new ThrowingSetterPerson(context);
        var car = new Car(context);

        var first = CreateChange(person, nameof(Person.FirstName), "John");
        var failing = CreateChange(throwing, nameof(ThrowingSetterPerson.Name), "Boom");
        var third = CreateChange(car, nameof(Car.Name), "Tesla");
        var excluded = CreateChange(person, nameof(Person.LastName), "Doe");

        SubjectPropertyChange[] changes = [first, failing, third, excluded];
        var exclude = new List<SubjectPropertyChange> { excluded };

        // Act
        var (successful, failed, errors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes, exclude);

        // Assert
        Assert.Equal("John", person.FirstName); // applied
        Assert.Null(person.LastName); // excluded, not applied
        Assert.Equal("Tesla", car.Name); // applied
        Assert.Contains(failing, failed);
        Assert.NotEmpty(errors);
        Assert.Contains(first, successful);
        Assert.Contains(third, successful);
        Assert.DoesNotContain(failing, successful);
        Assert.DoesNotContain(excluded, successful);
    }

    [Fact]
    public void WhenSubstituteByPropertyWithFewReplacements_ThenMatchingEntriesAreReplacedInPlace()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firstName = new PropertyReference(person, nameof(Person.FirstName));
        var lastName = new PropertyReference(person, nameof(Person.LastName));
        var changes = new[]
        {
            SubjectPropertyChange.Create(firstName, null, DateTimeOffset.UtcNow, null, "a", "b"),
            SubjectPropertyChange.Create(lastName, null, DateTimeOffset.UtcNow, null, "c", "d"),
        };
        var source = new object();
        var replacements = new[] { changes[0].WithSource(source) };

        // Act
        SubjectPropertyChangeOperations.SubstituteByProperty(changes.AsSpan(), replacements);

        // Assert
        Assert.Same(source, changes[0].Source);
        Assert.Equal("b", changes[0].GetNewValue<string>());
        Assert.Null(changes[1].Source);
    }

    [Fact]
    public void WhenSubstituteByPropertyWithEqualCounts_ThenAlignedEntriesAreReplacedPositionally()
    {
        // Arrange: equal counts in matching order exercise the positional fast path
        var context = CreateContext();
        var source = new object();
        var changes = new SubjectPropertyChange[10];
        var people = new Person[10];
        for (var i = 0; i < 10; i++)
        {
            people[i] = new Person(context);
            var property = new PropertyReference(people[i], nameof(Person.FirstName));
            changes[i] = SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, "old" + i, "new" + i);
        }
        var replacements = changes.Select(c => c.WithSource(source)).ToArray();

        // Act
        SubjectPropertyChangeOperations.SubstituteByProperty(changes.AsSpan(), replacements);

        // Assert: every slot replaced, order and values unchanged
        for (var i = 0; i < 10; i++)
        {
            Assert.Same(source, changes[i].Source);
            Assert.Equal("new" + i, changes[i].GetNewValue<string>());
            Assert.Same(people[i], changes[i].Property.Subject);
        }
    }

    [Fact]
    public void WhenSubstituteByPropertyWithEqualCountsButDifferentOrder_ThenEntriesAreReplacedByProperty()
    {
        // Arrange: equal counts but reversed order break the positional alignment, so the
        // general by-property scan must take over.
        var context = CreateContext();
        var person = new Person(context);
        var firstName = new PropertyReference(person, nameof(Person.FirstName));
        var lastName = new PropertyReference(person, nameof(Person.LastName));
        var changes = new[]
        {
            SubjectPropertyChange.Create(firstName, null, DateTimeOffset.UtcNow, null, "a", "b"),
            SubjectPropertyChange.Create(lastName, null, DateTimeOffset.UtcNow, null, "c", "d"),
        };
        var source = new object();
        var replacements = new[] { changes[1].WithSource(source), changes[0].WithSource(source) };

        // Act
        SubjectPropertyChangeOperations.SubstituteByProperty(changes.AsSpan(), replacements);

        // Assert: each entry got the replacement for its own property, order preserved
        Assert.Same(source, changes[0].Source);
        Assert.Equal("b", changes[0].GetNewValue<string>());
        Assert.Equal(firstName, changes[0].Property);
        Assert.Same(source, changes[1].Source);
        Assert.Equal("d", changes[1].GetNewValue<string>());
        Assert.Equal(lastName, changes[1].Property);
    }

    [Fact]
    public void WhenSubstituteByPropertyWithEmptyReplacements_ThenNothingChanges()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var changes = new[] { SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, "a", "b") };

        // Act
        SubjectPropertyChangeOperations.SubstituteByProperty(changes.AsSpan(), []);

        // Assert
        Assert.Null(changes[0].Source);
    }
}
