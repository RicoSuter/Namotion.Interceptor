using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Validation.Tests.Models;

namespace Namotion.Interceptor.Validation.Tests;

public class DerivedPropertyValidationTests
{
    private static IInterceptorSubjectContext CreateContext(IPropertyValidator validator)
    {
        return InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => validator);
    }

    [Fact]
    public void WhenDependencyIsAppliedFromSource_ThenDerivedRecalculationIsNotValidatedAndIsPublished()
    {
        // Arrange
        var validator = new RejectingValidator(nameof(DerivedDevice.Computed));
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);
        var publishedValues = new List<string?>();
        using var subscription = new PropertyReference(device, nameof(DerivedDevice.Computed))
            .SubscribeInline((in SubjectPropertyChange change) => publishedValues.Add(change.GetNewValue<string?>()));
        validator.Calls.Clear();

        // Act
        new PropertyReference(device, nameof(DerivedDevice.Input))
            .SetValueFromSource(new object(), DateTimeOffset.UtcNow, null, 2);

        // Assert
        Assert.Equal(2, device.Input);
        Assert.Equal("b", device.Computed);
        Assert.Equal(["b"], publishedValues);
        Assert.Equal([nameof(DerivedDevice.Input)], validator.Calls);
    }

    [Fact]
    public void WhenSettableDerivedPropertyIsWritten_ThenOnlyTheWriteIsValidated()
    {
        // Arrange
        var validator = new RejectingValidator();
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);
        validator.Calls.Clear();

        // Act
        device.Label = "x";

        // Assert
        Assert.Equal("x", device.Label);
        Assert.Equal("Label: x", device.LabelWithPrefix);
        // Neither the recalculation of Label itself nor the one of LabelWithPrefix is validated.
        Assert.Equal([nameof(DerivedDevice.Label)], validator.Calls);
    }

    [Fact]
    public void WhenWriteToSettableDerivedPropertyIsRejected_ThenValueIsNotStored()
    {
        // Arrange
        var validator = new RejectingValidator(nameof(DerivedDevice.Label));
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);

        // Act & Assert
        Assert.Throws<PropertyValidationException>(() => device.Label = "x");
        Assert.Null(device.Label);
        Assert.Equal("Label: ", device.LabelWithPrefix);
    }

    [Fact]
    public void WhenDependencyOfDynamicSettableDerivedPropertyIsWritten_ThenRecalculationIsNotValidated()
    {
        // Arrange
        var validator = new RejectingValidator("InputText");
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);
        var inputText = device.TryGetRegisteredSubject()!.AddDerivedProperty<string?>(
            "InputText",
            getValue: subject => ((DerivedDevice)subject).Input.ToString(CultureInfo.InvariantCulture),
            setValue: (subject, value) => ((DerivedDevice)subject).Input = int.Parse(value!, CultureInfo.InvariantCulture));
        var publishedValues = new List<string?>();
        using var subscription = inputText.Reference
            .SubscribeInline((in SubjectPropertyChange change) => publishedValues.Add(change.GetNewValue<string?>()));
        validator.Calls.Clear();

        // Act
        device.Input = 5;

        // Assert
        Assert.Equal("5", inputText.GetValue());
        Assert.Equal(["5"], publishedValues);
        Assert.Equal([nameof(DerivedDevice.Input)], validator.Calls);
    }

    [Fact]
    public void WhenDerivedPropertyIsAddedAtRuntime_ThenItsInitialValueIsNotValidatedAndIsPublished()
    {
        // Arrange
        var validator = new RejectingValidator("InputText");
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);
        device.Input = 2;
        var publishedValues = new List<string?>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name == "InputText")
                {
                    publishedValues.Add(change.GetNewValue<string?>());
                }
            });
        validator.Calls.Clear();

        // Act
        var inputText = device.TryGetRegisteredSubject()!.AddDerivedProperty<string?>(
            "InputText",
            getValue: subject => ((DerivedDevice)subject).Input.ToString(CultureInfo.InvariantCulture));

        // Assert
        Assert.Equal("2", inputText.GetValue());
        Assert.Equal(["2"], publishedValues);
        Assert.DoesNotContain("InputText", validator.Calls);
    }

    [Fact]
    public void WhenNonDerivedPropertyIsAddedAtRuntimeWithRejectedValue_ThenAddPropertyThrows()
    {
        // Arrange
        var validator = new RejectingValidator("Code");
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);

        // Act & Assert
        Assert.Throws<PropertyValidationException>(
            () => device.TryGetRegisteredSubject()!.AddProperty("Code", typeof(string), _ => "invalid", null));
    }

    /// <summary>Records the name of every validated property and rejects the given properties.</summary>
    private sealed class RejectingValidator(params string[] rejectedProperties) : IPropertyValidator
    {
        public List<string> Calls { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            Calls.Add(context.Property.Name);
            return rejectedProperties.Contains(context.Property.Name)
                ? [new ValidationResult($"{context.Property.Name} is rejected.")]
                : [];
        }
    }
}
