using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
    public void WhenSettableDerivedPropertyIsWritten_ThenOnlyTheAttemptedValueIsValidated()
    {
        // Arrange
        var validator = new RejectingValidator { IsRejecting = false };
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
        var validator = new RejectingValidator("InputText") { IsRejecting = false };
        var context = CreateContext(validator);
        var device = new DerivedDevice(context);
        // AddDerivedProperty publishes the initial value through the write chain, so rejecting starts afterwards.
        var inputText = device.TryGetRegisteredSubject()!.AddDerivedProperty<string?>(
            "InputText",
            getValue: subject => ((DerivedDevice)subject).Input.ToString(CultureInfo.InvariantCulture),
            setValue: (subject, value) => ((DerivedDevice)subject).Input = int.Parse(value!, CultureInfo.InvariantCulture));
        validator.IsRejecting = true;
        validator.Calls.Clear();

        // Act
        device.Input = 5;

        // Assert
        Assert.Equal("5", inputText.GetValue());
        Assert.Equal([nameof(DerivedDevice.Input)], validator.Calls);
    }

    /// <summary>Records the name of every validated property and rejects the given properties while <see cref="IsRejecting"/> is set.</summary>
    private sealed class RejectingValidator(params string[] rejectedProperties) : IPropertyValidator
    {
        public bool IsRejecting { get; set; } = true;

        public List<string> Calls { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            Calls.Add(context.Property.Name);
            return IsRejecting && rejectedProperties.Contains(context.Property.Name)
                ? [new ValidationResult($"{context.Property.Name} is rejected.")]
                : [];
        }
    }
}
