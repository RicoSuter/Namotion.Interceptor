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
        Assert.Throws<ValidationException>(() =>
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
        Assert.Throws<ValidationException>(() => person.LastName = "invalid");
        Assert.Null(person.LastName);

        // Act & Assert - the same value applied from a source is accepted
        new PropertyReference(person, nameof(Person.LastName))
            .SetValueFromSource(source, null, null, "invalid");
        Assert.Equal("invalid", person.LastName);
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
