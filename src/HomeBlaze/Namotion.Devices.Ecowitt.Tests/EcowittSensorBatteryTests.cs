using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Devices.Ecowitt.Sensors;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittSensorBatteryTests
{
    [Theory]
    [InlineData(48, 0, true)]
    [InlineData(48, 1, true)]
    [InlineData(48, 2, false)]
    [InlineData(48, 5, false)]
    [InlineData(48, 6, false)]
    [InlineData(48, 9, null)]
    [InlineData(7, 0, false)]
    [InlineData(7, 1, true)]
    public void WhenTheGatewayReportsABatteryValue_ThenIsBatteryLowFollowsTheEcowittThreshold(int typeCode, int battery, bool? expected)
    {
        // Arrange
        var sensor = CreateSensor();

        // Act
        sensor.Update(new EcowittSensorInfo { SensorId = "0x1", TypeCode = typeCode, Battery = battery, SignalLevel = 4 }, DateTimeOffset.UnixEpoch);

        // Assert
        Assert.Equal(expected, ((IBatteryState)sensor).IsBatteryLow);
    }

    [Fact]
    public void WhenTheSensorIsRegistered_ThenIsBatteryLowIsADiscreteDerivedStateAtTheContractPosition()
    {
        // Arrange
        var sensor = CreateSensor();

        // Act
        var property = sensor.TryGetRegisteredSubject()!.TryGetProperty(nameof(EcowittSensor.IsBatteryLow))!;

        // Assert
        var state = property.ReflectionAttributes.OfType<StateAttribute>().Single();
        Assert.True(property.Reference.Metadata.IsDerived);
        Assert.True(state.IsDiscrete);
        Assert.Equal(344, state.Position);
    }

    private static EcowittSensor CreateSensor()
    {
        var sensor = new EcowittSensor("0x1");
        ((IInterceptorSubject)sensor).Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());
        return sensor;
    }
}
