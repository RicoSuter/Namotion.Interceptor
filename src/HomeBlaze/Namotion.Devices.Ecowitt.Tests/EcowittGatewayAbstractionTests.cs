using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittGatewayAbstractionTests
{
    [Fact]
    public void WhenAirQualityAndLeakReadingsArrive_ThenTheSensorsReportThemThroughTheSharedContracts()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Co2 = [new EcowittCo2Data { Channel = 1, Co2 = 612m, Co2Avg24h = 580m }],
            Pm25 = [new EcowittPm25Data { Channel = 1, Pm25 = 12.5m, Pm25Avg24h = 10m }],
            Leaks = [new EcowittLeakData { Channel = 1, IsLeaking = true }]
        };

        // Act
        gateway.Update(data);

        // Assert
        ICarbonDioxideSensor carbonDioxideSensor = gateway.Co2Sensors[0];
        IPm25Sensor pm25Sensor = gateway.Pm25Sensors[0];
        IWaterLeakSensor leakSensor = gateway.LeakSensors[0];

        Assert.Equal(612m, carbonDioxideSensor.CarbonDioxide);
        Assert.Equal(580m, gateway.Co2Sensors[0].CarbonDioxideDailyAverage);
        Assert.Equal(12.5m, pm25Sensor.Pm25);
        Assert.Equal(10m, gateway.Pm25Sensors[0].Pm25DailyAverage);
        Assert.True(leakSensor.IsLeakDetected);
    }

    [Fact]
    public void WhenAirQualityAndLeakReadingsArrive_ThenTheRegisteredPropertiesCarryTheMetadataOfTheContracts()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Co2 = [new EcowittCo2Data { Channel = 1, Co2 = 612m }],
            Pm25 = [new EcowittPm25Data { Channel = 1, Pm25 = 12.5m }],
            Leaks = [new EcowittLeakData { Channel = 1, IsLeaking = true }]
        };

        // Act
        gateway.Update(data);

        // Assert
        var carbonDioxide = GetStateAttribute(gateway.Co2Sensors[0], nameof(ICarbonDioxideSensor.CarbonDioxide));
        var pm25 = GetStateAttribute(gateway.Pm25Sensors[0], nameof(IPm25Sensor.Pm25));
        var isLeakDetected = GetStateAttribute(gateway.LeakSensors[0], nameof(IWaterLeakSensor.IsLeakDetected));

        Assert.Equal(StateUnit.PartsPerMillion, carbonDioxide.Unit);
        Assert.Equal(StateUnit.MicrogramPerCubicMeter, pm25.Unit);
        Assert.True(isLeakDetected.IsDiscrete);
    }

    private static StateAttribute GetStateAttribute(IInterceptorSubject subject, string propertyName)
    {
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        return property.ReflectionAttributes.OfType<StateAttribute>().Single();
    }

    private static EcowittGateway CreateGateway()
    {
        var gateway = new EcowittGateway(new TestHttpClientFactory(), NullLogger<EcowittGateway>.Instance);
        ((IInterceptorSubject)gateway).Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());
        return gateway;
    }
}
