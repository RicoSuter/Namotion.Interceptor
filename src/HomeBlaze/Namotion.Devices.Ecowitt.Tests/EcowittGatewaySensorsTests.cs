using System.Reactive.Concurrency;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Devices.Ecowitt.Sensors;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
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
    public void WhenSensorDisappearsFromList_ThenItsContextNoLongerReachesTheGraph()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var removed = gateway.Sensors[OnlineWh68];
        var unpairedPage1 = EcowittSamples.SensorsInfoPage1Json.Replace("\"id\":\"51B\"", "\"id\":\"FFFFFFFF\"");

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo(unpairedPage1));

        // Assert
        Assert.Null(removed.TryGetRegisteredSubject());
        Assert.Null(((IInterceptorSubject)removed).TryGetContext()?.TryGetService<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenSensorIsReplacedByAnotherInTheSamePoll_ThenTheOldIsReleasedAndTheNewIsOwnedBySensors()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var removed = gateway.Sensors[OnlineWh68];
        var replacedPage1 = EcowittSamples.SensorsInfoPage1Json.Replace("\"id\":\"51B\"", "\"id\":\"51C\"");

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo(replacedPage1));

        // Assert
        var added = gateway.Sensors["0x51C"];
        Assert.False(gateway.Sensors.ContainsKey(OnlineWh68));
        Assert.Null(removed.TryGetRegisteredSubject());
        Assert.Null(((IInterceptorSubject)removed).TryGetContext()?.TryGetService<ILifecycleInterceptor>());
        Assert.Same(added, gateway.OutdoorSensor!.Sources["0x51C"]);
        Assert.Equal(2, added.TryGetRegisteredSubject()!.Parents.Length);
        Assert.NotNull(((IInterceptorSubject)added).TryGetContext()?.TryGetService<ILifecycleInterceptor>());
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

    [Theory]
    [InlineData(48, "WS90")]
    [InlineData(49, "WS85")]
    public void WhenPiezoSensorIsPaired_ThenItFeedsOutdoorAndPiezoRainAndHasThreeParents(int typeCode, string typeName)
    {
        // Arrange
        var gateway = CreateGateway();
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0x9637", TypeCode = typeCode, Battery = 4, Rssi = -48, SignalLevel = 4 }
        };
        var data = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { WindSpeed = 1.8m },
            PiezoRain = new EcowittRainData { DailyRain = 1.0m }
        };

        // Act
        gateway.Update(data, sensorsInfo);

        // Assert
        var sensor = gateway.Sensors["0x9637"];
        Assert.Equal(typeName, sensor.TypeName);
        Assert.Equal(0.8m, sensor.BatteryLevel);
        Assert.Same(sensor, Assert.Single(gateway.PiezoRainGauge!.Sources).Value);
        Assert.Same(sensor, Assert.Single(gateway.OutdoorSensor!.Sources).Value);
        var parents = sensor.TryGetRegisteredSubject()!.Parents;
        Assert.Equal(3, parents.Length);
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway));
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway.OutdoorSensor));
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway.PiezoRainGauge));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1, 0.0)]
    public void WhenWh25IsPaired_ThenIndoorSourcesHoldItWithItsBinaryBattery(int battery, decimal batteryLevel)
    {
        // Arrange
        var gateway = CreateGateway();
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0xAB", TypeCode = 4, SensorType = "wh25", Battery = battery, Rssi = -55, SignalLevel = 4 }
        };
        var data = new EcowittLiveData { Indoor = new EcowittIndoorData { Temperature = 19.3m } };

        // Act
        gateway.Update(data, sensorsInfo);

        // Assert
        var sensor = gateway.Sensors["0xAB"];
        Assert.Equal("WH25", sensor.TypeName);
        Assert.Equal(batteryLevel, sensor.BatteryLevel);
        Assert.Same(sensor, Assert.Single(gateway.IndoorSensor!.Sources).Value);
    }

    [Theory]
    [InlineData(new[] { "outdoor" }, true)]
    [InlineData(new[] { "piezo" }, true)]
    [InlineData(new[] { "outdoor", "piezo" }, false)]
    public void WhenWs90ReadingsAreHidden_ThenItIsLeftOutOnlyWhenOutdoorAndPiezoAreBothHidden(string[] hiddenSensors, bool isInSensors)
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.HiddenSensors = hiddenSensors;
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0x9637", TypeCode = 48, Rssi = -48, SignalLevel = 4 }
        };

        // Act
        gateway.Update(new EcowittLiveData(), sensorsInfo);

        // Assert
        Assert.Equal(isInSensors, gateway.Sensors.ContainsKey("0x9637"));
    }

    [Fact]
    public void WhenStateIsReset_ThenSensorsAreClearedAndDetached()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var sensor = gateway.Sensors[OnlineWh68];

        // Act
        gateway.ResetState();

        // Assert
        Assert.Empty(gateway.Sensors);
        Assert.Null(gateway.OutdoorSensor);
        Assert.Null(sensor.TryGetRegisteredSubject());
        Assert.Null(((IInterceptorSubject)sensor).TryGetContext()?.TryGetService<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenPollFails_ThenSensorsAreOfflineAndKeepLastSeen()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var sensor = gateway.Sensors[OnlineWh68];
        var lastSeen = sensor.LastSeen;

        // Act
        gateway.MarkSensorsOffline();

        // Assert
        Assert.False(sensor.IsOnline);
        Assert.Null(sensor.Rssi);
        Assert.Null(sensor.SignalStrength);
        Assert.Equal(1.0m, sensor.BatteryLevel);
        Assert.Equal(lastSeen, sensor.LastSeen);
    }

    [Fact]
    public void WhenSensorListCannotBeRead_ThenSensorsAreKept()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var sensors = gateway.Sensors;
        var sources = gateway.OutdoorSensor!.Sources;

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), sensorsInfo: null);

        // Assert
        Assert.Same(sensors, gateway.Sensors);
        Assert.Same(sources, gateway.OutdoorSensor.Sources);
    }

    [Fact]
    public void WhenReadingSubjectIsRebuilt_ThenItsSharedSensorStaysRegistered()
    {
        // Arrange
        var gateway = CreateGateway();
        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0xA6", TypeCode = 6, Battery = 0, Rssi = -69, SignalLevel = 3 }
        };
        gateway.Update(new EcowittLiveData { Channels = [new EcowittChannelData { Channel = 1 }] }, sensorsInfo);
        var sensor = gateway.Sensors["0xA6"];
        var originalChannel = gateway.ChannelSensors[0];

        // Act
        gateway.Update(new EcowittLiveData
        {
            Channels = [new EcowittChannelData { Channel = 1 }, new EcowittChannelData { Channel = 2 }]
        }, sensorsInfo);

        // Assert
        Assert.NotSame(originalChannel, gateway.ChannelSensors[0]);
        Assert.Same(sensor, Assert.Single(gateway.ChannelSensors[0].Sources).Value);
        Assert.Empty(gateway.ChannelSensors[1].Sources);
        var parents = sensor.TryGetRegisteredSubject()!.Parents;
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway));
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, gateway.ChannelSensors[0]));
    }

    [Fact]
    public void WhenSensorIsOnlineOnASecondPoll_ThenLastSeenAdvances()
    {
        // Arrange
        var clock = new TestClock { Now = new DateTimeOffset(2026, 10, 25, 19, 0, 0, TimeSpan.Zero) };
        var gateway = CreateGateway(clock);
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        clock.Now = clock.Now.AddSeconds(30);

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        Assert.Equal(clock.Now, gateway.Sensors[OnlineWh68].LastSeen);
        Assert.Null(gateway.Sensors[OfflineWs90].LastSeen);
    }

    [Fact]
    public void WhenSensorIsOnlineOrOffline_ThenIconColorIsSuccessOrError()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        Assert.Equal("Success", gateway.Sensors[OnlineWh68].IconColor);
        Assert.Equal("Error", gateway.Sensors[OfflineWs90].IconColor);
    }

    [Fact]
    public void WhenSensorGoesOfflineAndBackOnline_ThenIconColorChangesArePublished()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());
        var sensor = gateway.Sensors[OnlineWh68];
        var offlinePage1 = EcowittSamples.SensorsInfoPage1Json.Replace(
            "\"id\":\"51B\",\"batt\":\"5\",\"rssi\":\"-75\",\"signal\":\"4\"",
            "\"id\":\"51B\",\"batt\":\"9\",\"rssi\":\"--\",\"signal\":\"--\"");
        var changes = new List<string?>();
        using var subscription = ((IInterceptorSubject)gateway).GetContext()
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, sensor) && change.Property.Name == nameof(EcowittSensor.IconColor))
                {
                    changes.Add(change.GetNewValue<string?>());
                }
            });

        // Act
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo(offlinePage1));
        gateway.Update(EcowittSamples.ParseLiveData(), EcowittSamples.ParseSensorsInfo());

        // Assert
        Assert.Equal(["Error", "Success"], changes);
    }

    private static EcowittGateway CreateGateway(TimeProvider? clock = null)
    {
        var gateway = new EcowittGateway(new TestHttpClientFactory(), NullLogger<EcowittGateway>.Instance)
        {
            Clock = clock ?? TimeProvider.System
        };
        ((IInterceptorSubject)gateway).AttachToContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());
        return gateway;
    }
}
