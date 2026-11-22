using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Devices.Hyundai.Tests;

/// <summary>
/// Reads the metadata of the vehicle and its charge and climate children as the property panel does: from the state
/// and method metadata of the registered properties.
/// </summary>
public class BluelinkOperationMetadataTests
{
    [Theory]
    [InlineData("Charge", "StartCharging", "Start Charging", "EvStation", new StateUnit[0])]
    [InlineData("Charge", "StopCharging", "Stop Charging", "Stop", new StateUnit[0])]
    [InlineData("Charge", "SetChargeLimit", "Set Charge Limit", "BatteryChargingFull", new[] { StateUnit.Percent })]
    [InlineData("Charge", "SetChargeLimits", "Set Charge Limits", "BatteryChargingFull", new[] { StateUnit.Percent, StateUnit.Percent })]
    [InlineData("Climate", "StartClimate", "Start Climate", "Thermostat", new[] { StateUnit.DegreeCelsius })]
    [InlineData("Climate", "StartClimateWithDefrost", "Start Climate With Defrost", "AcUnit", new[] { StateUnit.DegreeCelsius })]
    [InlineData("Climate", "StopClimate", "Stop Climate", "Stop", new StateUnit[0])]
    public void WhenAChildOperationIsRegistered_ThenItHasNoCategoryAndKeepsItsTitleIconAndUnits(
        string child, string operation, string expectedTitle, string expectedIcon, StateUnit[] expectedUnits)
    {
        // Arrange
        var vehicle = CreateRegisteredVehicle();
        IInterceptorSubject subject = child == "Charge" ? vehicle.Charge : vehicle.Climate;

        // Act
        var metadata = subject.TryGetRegisteredSubject()!.TryGetProperty(operation)?.TryGetMethodMetadata();

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
        Assert.Equal(expectedTitle, metadata.Title);
        Assert.Equal(expectedIcon, metadata.Icon);
        Assert.Null(metadata.Category);
        Assert.Equal(
            expectedUnits.Cast<StateUnit?>(),
            metadata.Parameters.Where(parameter => !parameter.IsRuntimeProvided).Select(parameter => parameter.Unit));
    }

    [Fact]
    public void WhenTheVehicleIsRegistered_ThenTheModelIsSortedWithTheDeviceInformation()
    {
        // Arrange
        var vehicle = CreateRegisteredVehicle();
        var registeredVehicle = vehicle.TryGetRegisteredSubject()!;

        // Act
        var model = registeredVehicle.TryGetProperty(nameof(BluelinkVehicle.Model))?.GetStateMetadata();
        var manufacturer = registeredVehicle.TryGetProperty(nameof(BluelinkVehicle.Manufacturer))?.GetStateMetadata();

        // Assert
        Assert.NotNull(model);
        Assert.NotNull(manufacturer);
        Assert.Equal("Device", model.Category);
        Assert.Equal(manufacturer.Category, model.Category);
        Assert.Equal(manufacturer.Position + 1, model.Position);
    }

    private static BluelinkVehicle CreateRegisteredVehicle()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer)
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var vehicle = new BluelinkVehicle(
            new FakeVehicleCommands(),
            new BluelinkVehicleInfo("VEHICLE-1", "TESTVIN0000000001", "Kona", "KONA", "EV", BluelinkProtocol.Legacy));
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        return vehicle;
    }
}
