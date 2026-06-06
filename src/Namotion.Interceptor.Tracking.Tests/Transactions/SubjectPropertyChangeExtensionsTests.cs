using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectPropertyChangeExtensionsTests
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
    public void ApplyAllChanges_Span_NoExclude_AppliesAll()
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
        var (successful, failed, errors) = SubjectPropertyChangeExtensions.ApplyAllChanges(changes, exclude: null);

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
    public void ApplyAllChanges_Span_WithExclude_SkipsExcluded()
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
        var (successful, failed, errors) = SubjectPropertyChangeExtensions.ApplyAllChanges(changes, exclude);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Equal("Tesla", car.Name);
        Assert.Equal(2, successful.Count);
        Assert.Empty(failed);
        Assert.Empty(errors);
    }

    [Fact]
    public void ApplyAllChanges_Span_WithOutOfOrderExclude_SkipsExcludedViaHashSet()
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
        var (successful, failed, errors) = SubjectPropertyChangeExtensions.ApplyAllChanges(changes, exclude);

        // Assert
        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
        Assert.Equal(string.Empty, car.Name);
        Assert.Single(successful);
        Assert.Empty(failed);
        Assert.Empty(errors);
    }

    [Fact]
    public void ApplyAllChanges_Span_WhenApplyFails_ReportsFailedAndErrors()
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
        var (successful, failed, errors) = SubjectPropertyChangeExtensions.ApplyAllChanges(changes, exclude: null);

        // Assert
        Assert.Contains(failing, failed);
        Assert.NotEmpty(errors);
        Assert.Contains(first, successful);
        Assert.Contains(third, successful);
        Assert.DoesNotContain(failing, successful);
    }
}
