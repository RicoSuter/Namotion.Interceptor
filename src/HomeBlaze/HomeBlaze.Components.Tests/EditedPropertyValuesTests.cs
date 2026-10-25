using HomeBlaze.Components.Editors;
using HomeBlaze.Components.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class EditedPropertyValuesTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithDataAnnotationValidation();
    }

    private static EditedSubject CreateSubject()
    {
        return new EditedSubject(CreateContext())
        {
            Label = "Pump",
            Interval = 5,
            Limit = 3,
            Level = 2
        };
    }

    private static KeyValuePair<RegisteredSubjectProperty, object?> Edit(EditedSubject subject, string propertyName, object? value)
    {
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        return KeyValuePair.Create(property, value);
    }

    [Fact]
    public void WhenEveryValueCanBeConverted_ThenAllOfThemAreSet()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        var errors = EditedPropertyValues.Apply(
        [
            Edit(subject, nameof(EditedSubject.Label), "Fan"),
            Edit(subject, nameof(EditedSubject.Interval), "7"),
            Edit(subject, nameof(EditedSubject.Limit), null),
            Edit(subject, nameof(EditedSubject.Level), 2)
        ]);

        // Assert
        Assert.Empty(errors);
        Assert.Equal("Fan", subject.Label);
        Assert.Equal(7, subject.Interval);
        Assert.Null(subject.Limit);
        Assert.Equal(2, subject.Level);
    }

    [Fact]
    public void WhenOneValueCannotBeConverted_ThenNoValueIsSet()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        var errors = EditedPropertyValues.Apply(
        [
            Edit(subject, nameof(EditedSubject.Label), "Fan"),
            Edit(subject, nameof(EditedSubject.Tags), "not a list"),
            Edit(subject, nameof(EditedSubject.Interval), 7)
        ]);

        // Assert
        var error = Assert.Single(errors);
        Assert.StartsWith("Failed to set 'Tags': ", error);
        Assert.Equal("Pump", subject.Label);
        Assert.Null(subject.Tags);
        Assert.Equal(5, subject.Interval);
    }

    [Fact]
    public void WhenSeveralValuesCannotBeConverted_ThenEachOfThemIsReported()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        var errors = EditedPropertyValues.Apply(
        [
            Edit(subject, nameof(EditedSubject.Interval), "many"),
            Edit(subject, nameof(EditedSubject.Label), "Fan"),
            Edit(subject, nameof(EditedSubject.Tags), "not a list")
        ]);

        // Assert
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("Failed to set 'Interval': ", errors[0]);
        Assert.StartsWith("Failed to set 'Tags': ", errors[1]);
        Assert.Equal("Pump", subject.Label);
    }

    [Fact]
    public void WhenAPropertyThatMustHaveAValueGetsNone_ThenNoValueIsSet()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        var errors = EditedPropertyValues.Apply(
        [
            Edit(subject, nameof(EditedSubject.Label), "Fan"),
            Edit(subject, nameof(EditedSubject.Interval), null)
        ]);

        // Assert
        Assert.Equal(["Failed to set 'Interval': A value is required."], errors);
        Assert.Equal("Pump", subject.Label);
        Assert.Equal(5, subject.Interval);
    }

    [Fact]
    public void WhenASetterRejectsItsValue_ThenTheValuesSetBeforeItAreRestored()
    {
        // Arrange
        var subject = CreateSubject();

        // Act - the range of the level is checked when it is set, after the label and the interval were set.
        var errors = EditedPropertyValues.Apply(
        [
            Edit(subject, nameof(EditedSubject.Label), "Fan"),
            Edit(subject, nameof(EditedSubject.Interval), 7),
            Edit(subject, nameof(EditedSubject.Level), 99),
            Edit(subject, nameof(EditedSubject.Limit), 8)
        ]);

        // Assert
        var error = Assert.Single(errors);
        Assert.StartsWith("Failed to set 'Level': ", error);
        Assert.Equal("Pump", subject.Label);
        Assert.Equal(5, subject.Interval);
        Assert.Equal(2, subject.Level);
        Assert.Equal(3, subject.Limit);
    }

    [Fact]
    public void WhenAValueCannotBeSetBack_ThenItStaysChangedAndTheOtherValuesAreSetBack()
    {
        // Arrange - the level was never set, so it holds 0, which its own range does not allow.
        var subject = new EditedSubject(CreateContext()) { Label = "Pump" };

        // Act - the priority is rejected after the label and the level were set.
        var errors = EditedPropertyValues.Apply(
        [
            Edit(subject, nameof(EditedSubject.Label), "Fan"),
            Edit(subject, nameof(EditedSubject.Level), 5),
            Edit(subject, nameof(EditedSubject.Priority), 99)
        ]);

        // Assert
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("Failed to set 'Priority': ", errors[0]);
        Assert.StartsWith("Failed to restore 'Level': ", errors[1]);
        Assert.Equal(5, subject.Level);
        Assert.Equal("Pump", subject.Label);
        Assert.Equal(0, subject.Priority);
    }

    [Theory]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(TimeSpan), true)]
    [InlineData(typeof(DayOfWeek), true)]
    [InlineData(typeof(int?), false)]
    [InlineData(typeof(string), false)]
    [InlineData(typeof(string[]), false)]
    public void WhenTypeIsAValueTypeThatIsNotNullable_ThenItsPropertyMustHaveAValue(Type propertyType, bool expectedIsRequired)
    {
        // Act
        var isRequired = EditedPropertyValues.IsRequired(propertyType);

        // Assert
        Assert.Equal(expectedIsRequired, isRequired);
    }
}
