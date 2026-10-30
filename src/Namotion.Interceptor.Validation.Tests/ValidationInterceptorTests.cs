using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Validation.Tests.Models;

namespace Namotion.Interceptor.Validation.Tests;

public class ValidationInterceptorTests
{
    [Fact]
    public void ShouldValidateProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithDataAnnotationValidation();

        // Act
        var person = new Person(context)
        {
            FirstName = "Rico" // allowed
        };

        // Assert
        Assert.Throws<PropertyValidationException>(() =>
        {
            person.FirstName = "Suter"; // not allowed
        });
        Assert.Equal("Rico", person.FirstName);
    }

    [Fact]
    public void WhenValueComesFromSource_ThenProvenanceAwareValidatorCanSkipStrictValidation()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => new LocalOnlyValidator());

        var person = new Person(context);
        var source = new object();

        // Act & Assert - a local write of "invalid" is rejected
        Assert.Throws<PropertyValidationException>(() => person.LastName = "invalid");
        Assert.Null(person.LastName);

        // Act & Assert - the same value applied from a source is accepted
        new PropertyReference(person, nameof(Person.LastName))
            .SetValueFromSource(source, null, null, "invalid");
        Assert.Equal("invalid", person.LastName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WhenWriteIsInvalid_ThenExceptionKeepsEachValidationResult(int resultCount)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new FixedResultsValidator(resultCount));
        var person = new Person(context);

        // Act
        var exception = Assert.Throws<PropertyValidationException>(() => person.LastName = "invalid");

        // Assert
        var expectedMessages = new[] { "First error.", "Second error." }.Take(resultCount).ToArray();
        Assert.Equal(string.Join('\n', expectedMessages), exception.Message);
        Assert.Equal(expectedMessages, exception.ValidationResults.Select(result => result.ErrorMessage));
        Assert.Equal([nameof(Person.LastName)], exception.ValidationResults[0].MemberNames);
        Assert.Equal(nameof(Person.LastName), exception.Property.Name);
        Assert.Equal("invalid", exception.Value);
        Assert.Null(person.LastName);
    }

    /// <summary>
    /// Returns the first <paramref name="resultCount"/> of two fixed errors for the value "invalid".
    /// </summary>
    private sealed class FixedResultsValidator(int resultCount) : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Value is not "invalid")
            {
                return [];
            }

            return new[]
            {
                new ValidationResult("First error.", [context.Property.Name]),
                new ValidationResult("Second error.")
            }.Take(resultCount);
        }
    }

    /// <summary>
    /// Rejects the value "invalid" only for locally originated writes; writes whose origin is a
    /// source (or a transaction confirmation) are accepted, so provenance decides strictness.
    /// </summary>
    private sealed class LocalOnlyValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Origin.Kind == ChangeOriginKind.Local &&
                context.Value is string stringValue &&
                stringValue == "invalid")
            {
                return [new ValidationResult("Local writes may not set the value 'invalid'.")];
            }

            return [];
        }
    }
}
