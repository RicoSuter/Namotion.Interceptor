using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittGatewaySensorsTests
{
    private const string OfflineWs90 = "0x9637";
    private const string OnlineWh68 = "0x51B";
    private const string Wh40 = "0x1C183";

    [Fact]
    public void WhenOneOfTwoOutdoorSensorsIsOffline_ThenEachSensorHasItsOwnStatus()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        var ws90 = gateway.Sensors[OfflineWs90];
        Assert.Equal(OfflineWs90, ws90.SensorId);
        Assert.Equal(48, ws90.TypeCode);
        Assert.Equal("WS90", ws90.TypeName);
        Assert.Equal("Temp & Humidity & Solar & Wind & Rain", ws90.TypeDescription);
        Assert.False(ws90.IsOnline);
        Assert.Null(ws90.BatteryLevel);
        Assert.Null(ws90.SignalStrength);
        Assert.Null(ws90.Rssi);
        Assert.Null(ws90.LastSeen);

        var wh68 = gateway.Sensors[OnlineWh68];
        Assert.Equal(1, wh68.TypeCode);
        Assert.Equal("WH68", wh68.TypeName);
        Assert.Equal("Solar & Wind", wh68.TypeDescription);
        Assert.True(wh68.IsOnline);
        Assert.Equal(1.0m, wh68.BatteryLevel);
        Assert.Equal(4, wh68.SignalStrength);
        Assert.Equal(-75, wh68.Rssi);
        Assert.NotNull(wh68.LastSeen);

        var wh40 = gateway.Sensors[Wh40];
        Assert.Equal("WH40", wh40.TypeName);
        Assert.True(wh40.IsOnline);
        Assert.Equal(0.4m, wh40.BatteryLevel);
        Assert.Equal(-69, wh40.Rssi);
    }

    [Fact]
    public void WhenOneOfTwoOutdoorSensorsIsOffline_ThenOutdoorHasTheMergedValuesAndBothAsSources()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        var outdoor = gateway.OutdoorSensor!;
        Assert.Null(outdoor.Temperature);
        Assert.Null(outdoor.Humidity);
        Assert.Equal(3.3m, outdoor.WindSpeed);
        Assert.Equal(102m, outdoor.WindDirection);
        Assert.Equal(0m, outdoor.Illuminance);
        Assert.Equal([OnlineWh68, OfflineWs90], outdoor.Sources.Keys.Order(StringComparer.Ordinal));
        Assert.Same(gateway.Sensors[OfflineWs90], outdoor.Sources[OfflineWs90]);
        Assert.Same(gateway.Sensors[OnlineWh68], outdoor.Sources[OnlineWh68]);
        Assert.Same(gateway.Sensors[Wh40], Assert.Single(gateway.RainGauge!.Sources).Value);
        Assert.Empty(gateway.IndoorSensor!.Sources);
        Assert.Null(gateway.PiezoRainGauge);
    }

    [Fact]
    public void WhenSlotsAreLearningOrDisabled_ThenTheyHaveNoSensor()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        Assert.Equal([Wh40, OnlineWh68, OfflineWs90], gateway.Sensors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WhenSensorGoesOffline_ThenLastSeenStaysAtTheLastReception()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var sensor = gateway.Sensors[OnlineWh68];
        var lastSeen = sensor.LastSeen;
        var offlinePage1 = EcowittSamples.SensorsInfoPage1Json.Replace(
            "\"id\":\"51B\",\"batt\":\"5\",\"rssi\":\"-75\",\"signal\":\"4\"",
            "\"id\":\"51B\",\"batt\":\"9\",\"rssi\":\"--\",\"signal\":\"--\"");

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo(offlinePage1));

        // Assert
        Assert.Same(sensor, gateway.Sensors[OnlineWh68]);
        Assert.False(sensor.IsOnline);
        Assert.Null(sensor.BatteryLevel);
        Assert.Null(sensor.Rssi);
        Assert.NotNull(lastSeen);
        Assert.Equal(lastSeen, sensor.LastSeen);
    }

    [Fact]
    public void WhenSensorListIsUnchanged_ThenSensorsAndSourcesAreKept()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var sensors = gateway.Sensors;
        var sources = gateway.OutdoorSensor!.Sources;

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        Assert.Same(sensors, gateway.Sensors);
        Assert.Same(sources, gateway.OutdoorSensor.Sources);
    }

    [Fact]
    public void WhenSensorDisappearsFromList_ThenItIsRemovedFromSensorsAndSourcesAndDetached()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var removed = gateway.Sensors[OnlineWh68];
        var kept = gateway.Sensors[OfflineWs90];
        var unpairedPage1 = EcowittSamples.SensorsInfoPage1Json.Replace("\"id\":\"51B\"", "\"id\":\"FFFFFFFF\"");

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo(unpairedPage1));

        // Assert
        Assert.False(gateway.Sensors.ContainsKey(OnlineWh68));
        Assert.Same(kept, gateway.Sensors[OfflineWs90]);
        Assert.Same(kept, Assert.Single(gateway.OutdoorSensor!.Sources).Value);
        Assert.Null(removed.TryGetRegisteredSubject());
        Assert.NotNull(kept.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenSensorIsASource_ThenItHasTheSensorsAndTheReadingsAsParents()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        var parents = gateway.Sensors[OnlineWh68].TryGetRegisteredSubject()!.Parents;
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway));
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway.OutdoorSensor));
    }

    [Fact]
    public void WhenChannelSensorIsPaired_ThenItIsTheSourceOfItsChannelOnly()
    {
        // Arrange
        var gateway = CreateGateway();
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0xA6", TypeCode = 7, SensorType = "wh31", Battery = 0, Rssi = -69, SignalLevel = 3 }
        };
        var data = new EcowittLiveData
        {
            Channels =
            [
                new EcowittChannelData { Channel = 1, Temperature = 20.0m },
                new EcowittChannelData { Channel = 2, Temperature = 21.3m }
            ]
        };

        // Act
        gateway.Update(data, sensorsInfo);

        // Assert
        var sensor = gateway.Sensors["0xA6"];
        Assert.Equal("WH31", sensor.TypeName);
        Assert.Equal(1.0m, sensor.BatteryLevel); // binary battery: 0 = OK
        Assert.Empty(gateway.ChannelSensors[0].Sources);
        Assert.Same(sensor, Assert.Single(gateway.ChannelSensors[1].Sources).Value);
    }

    [Fact]
    public void WhenChannelIsHidden_ThenItsSensorIsLeftOut()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.HiddenSensors = ["ch:2", "outdoor"];
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0xA6", TypeCode = 7, Rssi = -69, SignalLevel = 3 },
            new EcowittSensorInfo { SensorId = "0x51B", TypeCode = 1, Rssi = -75, SignalLevel = 4 },
            new EcowittSensorInfo { SensorId = "0x1C183", TypeCode = 3, Rssi = -69, SignalLevel = 4 }
        };

        // Act
        gateway.Update(new EcowittLiveData(), sensorsInfo);

        // Assert
        Assert.Equal(["0x1C183"], gateway.Sensors.Keys);
    }

    [Fact]
    public void WhenTypeCodeIsNotMapped_ThenTypeNameComesFromTheImageKey()
    {
        // Arrange
        var gateway = CreateGateway();
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0x77", TypeCode = 99, SensorType = "wh99", Rssi = -60, SignalLevel = 4 }
        };

        // Act
        gateway.Update(new EcowittLiveData(), sensorsInfo);

        // Assert
        Assert.Equal("WH99", gateway.Sensors["0x77"].TypeName);
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

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
