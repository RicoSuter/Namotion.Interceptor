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
        var validator = new RecordingValidator(nameof(Person.LastName));
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
        var validator = new RecordingValidator(nameof(Person.LastName));
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

    [Fact]
    public void WhenSourceWriteTriggersDerivedRecalculation_ThenTheDerivedWriteIsStillValidated()
    {
        // Arrange: the source write itself skips validation, but FullName depends on LastName and its
        // recalculation is a local write, so it keeps its veto. Documented behavior, pinned here
        // because the asymmetry is easy to break by accident.
        var validator = new RecordingValidator(nameof(Person.FullName));
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => validator);

        var person = new Person(context);
        var source = new object();

        // Act & Assert
        Assert.Throws<ValidationException>(() =>
            new PropertyReference(person, nameof(Person.LastName))
                .SetValueFromSource(source, null, null, "anything"));

        Assert.Equal([ChangeOriginKind.Local], validator.SeenOrigins);

        // The asymmetry this test is named for: the source write itself was never vetoed, it landed.
        Assert.Equal("anything", person.LastName);
    }

    /// <summary>
    /// Rejects every write to one property and records the origin of each write it is asked about for
    /// that property, so a test can assert not just that a value landed but that the validator was
    /// never consulted. Scoped to a single property because a write also triggers derived
    /// recalculations, which are local by design and would otherwise register here.
    /// </summary>
    private sealed class RecordingValidator(string propertyName) : IPropertyValidator
    {
        public List<ChangeOriginKind> SeenOrigins { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Property.Name != propertyName)
            {
                return [];
            }

            SeenOrigins.Add(context.Origin.Kind);
            return [new ValidationResult("Rejected unconditionally.")];
        }
    }
}
