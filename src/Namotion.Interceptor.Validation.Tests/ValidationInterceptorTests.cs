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
    public void WhenOriginIsLocal_ThenValidatorIsInvokedAndRejects()
    {
        // Arrange
        var validator = new RecordingValidator();
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => validator);

        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ValidationException>(() => person.LastName = "anything");
        Assert.Null(person.LastName);
        Assert.Equal([ChangeOriginKind.Local], validator.SeenOrigins);
    }

    [Fact]
    public void WhenOriginIsFromSource_ThenValidatorIsNotInvokedAtAll()
    {
        // Arrange: a validator that rejects unconditionally, so only the skip can let the write through.
        var validator = new RecordingValidator();
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => validator);

        var person = new Person(context);
        var source = new object();

        // Act
        new PropertyReference(person, nameof(Person.LastName))
            .SetValueFromSource(source, null, null, "anything");

        // Assert: the value lands, and the skip happened before validator resolution.
        Assert.Equal("anything", person.LastName);
        Assert.Empty(validator.SeenOrigins);
    }

    [Fact]
    public void WhenOriginIsFromSource_ThenDataAnnotationsAreNotEnforced()
    {
        // Arrange: FirstName carries [MaxLength(4)].
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithDataAnnotationValidation()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var source = new object();

        // Act: a source sends a value the local annotation would reject.
        new PropertyReference(person, nameof(Person.FirstName))
            .SetValueFromSource(source, null, null, "Suter");

        // Assert: the model mirrors the source rather than diverging from it.
        Assert.Equal("Suter", person.FirstName);
    }

    /// <summary>
    /// Rejects every write and records the origin of each write it was asked about, so a test can
    /// assert not just that a value landed but that the validator was never consulted.
    /// </summary>
    private sealed class RecordingValidator : IPropertyValidator
    {
        public List<ChangeOriginKind> SeenOrigins { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            SeenOrigins.Add(context.Origin.Kind);
            return [new ValidationResult("Rejected unconditionally.")];
        }
    }
}
