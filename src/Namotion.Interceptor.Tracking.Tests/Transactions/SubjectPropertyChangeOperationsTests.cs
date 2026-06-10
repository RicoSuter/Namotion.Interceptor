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
    public void WhenTrySubstituteAtScatteredIndices_ThenAddressedEntriesAreReplaced()
    {
        // Arrange: source-bound changes sit at indices 0 and 2; index 1 is a local change left untouched.
        var context = CreateContext();
        var person = new Person(context);
        var car = new Car(context);
        var firstName = new PropertyReference(person, nameof(Person.FirstName));
        var lastName = new PropertyReference(person, nameof(Person.LastName));
        var carName = new PropertyReference(car, nameof(Car.Name));
        var changes = new[]
        {
            SubjectPropertyChange.Create(firstName, null, DateTimeOffset.UtcNow, null, "a", "b"),
            SubjectPropertyChange.Create(lastName, null, DateTimeOffset.UtcNow, null, "c", "d"),
            SubjectPropertyChange.Create(carName, null, DateTimeOffset.UtcNow, null, "e", "f"),
        };
        var source = new object();
        var replacements = new[] { changes[0].WithSource(source), changes[2].WithSource(source) };
        var indices = new[] { 0, 2 };

        // Act
        var result = SubjectPropertyChangeOperations.TrySubstituteAtIndices(changes.AsSpan(), replacements, indices);

        // Assert
        Assert.True(result);
        Assert.Same(source, changes[0].Source);
        Assert.Equal("b", changes[0].GetNewValue<string>());
        Assert.Null(changes[1].Source);
        Assert.Same(source, changes[2].Source);
        Assert.Equal("f", changes[2].GetNewValue<string>());
    }

    [Fact]
    public void WhenTrySubstituteWithCountMismatch_ThenReturnsFalseAndLeavesSpanUntouched()
    {
        // Arrange: one replacement but two indices.
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
        var indices = new[] { 0, 1 };

        // Act
        var result = SubjectPropertyChangeOperations.TrySubstituteAtIndices(changes.AsSpan(), replacements, indices);

        // Assert
        Assert.False(result);
        Assert.Null(changes[0].Source);
        Assert.Null(changes[1].Source);
    }

    [Fact]
    public void WhenTrySubstituteWithOutOfRangeIndex_ThenReturnsFalseAndLeavesSpanUntouched()
    {
        // Arrange: the index addresses a slot past the end of the span.
        var context = CreateContext();
        var person = new Person(context);
        var firstName = new PropertyReference(person, nameof(Person.FirstName));
        var changes = new[]
        {
            SubjectPropertyChange.Create(firstName, null, DateTimeOffset.UtcNow, null, "a", "b"),
        };
        var source = new object();
        var replacements = new[] { changes[0].WithSource(source) };
        var indices = new[] { 5 };

        // Act
        var result = SubjectPropertyChangeOperations.TrySubstituteAtIndices(changes.AsSpan(), replacements, indices);

        // Assert
        Assert.False(result);
        Assert.Null(changes[0].Source);
    }

    [Fact]
    public void WhenTrySubstituteWithPropertyMismatch_ThenReturnsFalseAndLeavesSpanUntouched()
    {
        // Arrange: the index addresses an entry with a different property than the replacement.
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
        // Replacement carries firstName but the index points at lastName.
        var replacements = new[] { changes[0].WithSource(source) };
        var indices = new[] { 1 };

        // Act
        var result = SubjectPropertyChangeOperations.TrySubstituteAtIndices(changes.AsSpan(), replacements, indices);

        // Assert
        Assert.False(result);
        Assert.Null(changes[0].Source);
        Assert.Null(changes[1].Source);
    }

    [Fact]
    public void WhenSecondEntryMismatches_ThenFirstEntryIsLeftUntouched()
    {
        // Arrange: the first entry validates, the second mismatches (property identity). Validation runs
        // before any write, so the first entry must remain untouched.
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
        // First replacement aligns at index 0; second carries lastName but is pointed at index 0 (mismatch).
        var replacements = new[] { changes[0].WithSource(source), changes[1].WithSource(source) };
        var indices = new[] { 0, 0 };

        // Act
        var result = SubjectPropertyChangeOperations.TrySubstituteAtIndices(changes.AsSpan(), replacements, indices);

        // Assert
        Assert.False(result);
        Assert.Null(changes[0].Source); // first entry untouched despite aligning, because validation failed first
        Assert.Null(changes[1].Source);
    }
}
