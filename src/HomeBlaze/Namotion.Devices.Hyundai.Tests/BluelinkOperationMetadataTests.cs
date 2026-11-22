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
/// Reads the operations of the charge and climate children as the property panel does: from the method metadata of the
/// property that each operation is registered as.
/// </summary>
public class BluelinkOperationMetadataTests
{
    [Theory]
    [InlineData("Charge", "StartCharging", "Start Charging")]
    [InlineData("Charge", "StopCharging", "Stop Charging")]
    [InlineData("Charge", "SetChargeLimit", "Set Charge Limit")]
    [InlineData("Charge", "SetChargeLimits", "Set Charge Limits")]
    [InlineData("Climate", "StartClimate", "Start Climate")]
    [InlineData("Climate", "StartClimateWithDefrost", "Start Climate With Defrost")]
    [InlineData("Climate", "StopClimate", "Stop Climate")]
    public void WhenAChildOperationIsRegistered_ThenItHasNoCategoryAndKeepsItsTitle(string child, string operation, string expectedTitle)
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
        Assert.NotNull(metadata.Icon);
        Assert.Null(metadata.Category);
    }

    private static BluelinkVehicle CreateRegisteredVehicle()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        var vehicle = new BluelinkVehicle(
            new FakeVehicleCommands(),
            new BluelinkVehicleInfo("VEHICLE-1", "TESTVIN0000000001", "Kona", "KONA", "EV", BluelinkProtocol.Legacy));
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        return vehicle;
    }
}
