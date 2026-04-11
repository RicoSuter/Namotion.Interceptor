using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Ecowitt.Models;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittGatewayTests
{
    private static EcowittGateway CreateGateway()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new EcowittGateway(httpClientFactory, NullLogger<EcowittGateway>.Instance);
    }

    [Fact]
    public void WhenLiveDataHasOutdoor_ThenCreatesOutdoorSensor()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData
            {
                Temperature = 18.1m,
                Humidity = 0.44m,
                WindSpeed = 1.8m,
                WindDirection = 99m,
                UvIndex = 3m
            }
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.NotNull(gateway.OutdoorSensor);
        Assert.Equal(18.1m, gateway.OutdoorSensor!.Temperature);
        Assert.Equal(0.44m, gateway.OutdoorSensor.Humidity);
        Assert.Equal(1.8m, gateway.OutdoorSensor.WindSpeed);
        Assert.Equal(99m, gateway.OutdoorSensor.WindDirection);
        Assert.Equal(3m, gateway.OutdoorSensor.UvIndex);
    }

    [Fact]
    public void WhenLiveDataHasIndoor_ThenCreatesIndoorSensor()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Indoor = new EcowittIndoorData
            {
                Temperature = 19.3m,
                Humidity = 0.46m,
                AbsolutePressure = 946.2m,
                RelativePressure = 1008.3m
            }
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.NotNull(gateway.IndoorSensor);
        Assert.Equal(19.3m, gateway.IndoorSensor!.Temperature);
        Assert.Equal(946.2m, gateway.IndoorSensor.AbsolutePressure);
    }

    [Fact]
    public void WhenLiveDataHasRain_ThenCreatesRainGauge()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Rain = new EcowittRainData
            {
                RainRate = 0.0m,
                DailyRain = 3.8m,
                YearlyRain = 56.5m
            }
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.NotNull(gateway.RainGauge);
        Assert.Equal(0.0m, gateway.RainGauge!.RainRate);
        Assert.Equal(3.8m, gateway.RainGauge.DailyRain);
        Assert.Equal(56.5m, gateway.RainGauge.YearlyRain);
    }

    [Fact]
    public void WhenYearlyRainIncreases_ThenTotalRainAccumulates()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act — first poll: yearly rain is 50mm
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Assert
        Assert.Equal(50m, gateway.RainGauge!.TotalRain);

        // Act — second poll: yearly rain increased to 60mm
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 60m }
        });

        // Assert — total follows the bucket
        Assert.Equal(60m, gateway.RainGauge.TotalRain);
    }

    [Fact]
    public void WhenYearlyRainResets_ThenTotalRainContinuesAccumulating()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 150m }
        });
        Assert.Equal(150m, gateway.RainGauge!.TotalRain);

        // Act — year rolls over, yearly counter resets to 5mm
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 5m }
        });

        // Assert — total = previous 150 + new 5 = 155
        Assert.Equal(155m, gateway.RainGauge.TotalRain);
        Assert.Equal(150m, gateway.RainCumulativeOffset);
    }

    [Fact]
    public void WhenYearlyRainResetsMultipleTimes_ThenOffsetAccumulates()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 100m }
        });

        // Act — first reset
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 20m }
        });

        // Act — second reset
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 10m }
        });

        // Assert — total = 100 + 20 + 10 = 130
        Assert.Equal(130m, gateway.RainGauge!.TotalRain);
        Assert.Equal(120m, gateway.RainCumulativeOffset);
    }

    [Fact]
    public void WhenLiveDataHasChannels_ThenCreatesChannelSensors()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Channels =
            [
                new EcowittChannelData { Channel = 1, Temperature = 21.3m, Humidity = 0.44m },
                new EcowittChannelData { Channel = 3, Temperature = 19.8m, Humidity = 0.52m }
            ]
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Equal(2, gateway.ChannelSensors.Length);
        Assert.Equal(1, gateway.ChannelSensors[0].Channel);
        Assert.Equal(21.3m, gateway.ChannelSensors[0].Temperature);
        Assert.Equal(3, gateway.ChannelSensors[1].Channel);
    }

    [Fact]
    public void WhenSecondUpdateWithSameChannels_ThenUpdatesInPlace()
    {
        // Arrange
        var gateway = CreateGateway();
        var data1 = new EcowittLiveData
        {
            Channels = [new EcowittChannelData { Channel = 1, Temperature = 21.3m }]
        };
        gateway.UpdateFromLiveData(data1);
        var originalSensor = gateway.ChannelSensors[0];

        var data2 = new EcowittLiveData
        {
            Channels = [new EcowittChannelData { Channel = 1, Temperature = 22.0m }]
        };

        // Act
        gateway.UpdateFromLiveData(data2);

        // Assert
        Assert.Same(originalSensor, gateway.ChannelSensors[0]);
        Assert.Equal(22.0m, gateway.ChannelSensors[0].Temperature);
    }

    [Fact]
    public void WhenSecondUpdateWithDifferentChannelCount_ThenReplacesArray()
    {
        // Arrange
        var gateway = CreateGateway();
        var data1 = new EcowittLiveData
        {
            Channels = [new EcowittChannelData { Channel = 1 }]
        };
        gateway.UpdateFromLiveData(data1);
        var originalSensor = gateway.ChannelSensors[0];

        var data2 = new EcowittLiveData
        {
            Channels =
            [
                new EcowittChannelData { Channel = 1 },
                new EcowittChannelData { Channel = 2 }
            ]
        };

        // Act
        gateway.UpdateFromLiveData(data2);

        // Assert
        Assert.Equal(2, gateway.ChannelSensors.Length);
        Assert.NotSame(originalSensor, gateway.ChannelSensors[0]);
    }

    [Fact]
    public void WhenChannelIsHidden_ThenNotCreated()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.HiddenSensors = ["ch:3"];
        var data = new EcowittLiveData
        {
            Channels =
            [
                new EcowittChannelData { Channel = 1, Temperature = 21.3m },
                new EcowittChannelData { Channel = 3, Temperature = 19.8m }
            ]
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Single(gateway.ChannelSensors);
        Assert.Equal(1, gateway.ChannelSensors[0].Channel);
    }

    [Fact]
    public void WhenSectionsMissing_ThenSensorsStayNull()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData();

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Null(gateway.OutdoorSensor);
        Assert.Null(gateway.IndoorSensor);
        Assert.Null(gateway.RainGauge);
        Assert.Null(gateway.PiezoRainGauge);
        Assert.Null(gateway.LightningSensor);
        Assert.Empty(gateway.ChannelSensors);
    }

    [Fact]
    public void WhenOutdoorUpdatedTwice_ThenSameInstance()
    {
        // Arrange
        var gateway = CreateGateway();
        var data1 = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { Temperature = 18.0m }
        };
        gateway.UpdateFromLiveData(data1);
        var original = gateway.OutdoorSensor;

        var data2 = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { Temperature = 19.0m }
        };

        // Act
        gateway.UpdateFromLiveData(data2);

        // Assert
        Assert.Same(original, gateway.OutdoorSensor);
        Assert.Equal(19.0m, gateway.OutdoorSensor!.Temperature);
    }

    [Fact]
    public void WhenLightningPresent_ThenCreatesLightningSensor()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Lightning = new EcowittLightningData
            {
                Distance = 15m,
                StrikeCount = 3,
                Battery = 5
            }
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.NotNull(gateway.LightningSensor);
        Assert.Equal(15m, gateway.LightningSensor!.Distance);
        Assert.Equal(3, gateway.LightningSensor.StrikeCount);
    }

    [Fact]
    public void WhenRainSensorHiddenAfterCreation_ThenNulledOut()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Rain = new EcowittRainData { DailyRain = 3.8m }
        };
        gateway.UpdateFromLiveData(data);
        Assert.NotNull(gateway.RainGauge);

        // Act
        gateway.HiddenSensors = ["rain"];
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Null(gateway.RainGauge);
    }

    [Fact]
    public void WhenSensorInfoProvided_ThenMapsToCorrectSensors()
    {
        // Arrange
        var gateway = CreateGateway();
        var liveData = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { Temperature = 18.0m },
            Channels = [new EcowittChannelData { Channel = 1, Temperature = 21.0m }]
        };
        gateway.UpdateFromLiveData(liveData);

        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0x9637", TypeCode = 48, Rssi = -48 },
            new EcowittSensorInfo { SensorId = "0xA6", TypeCode = 6, Rssi = -69 }
        };

        // Act
        gateway.UpdateSensorInfo(sensorsInfo);

        // Assert
        Assert.Equal("0x9637", gateway.OutdoorSensor!.SensorId);
        Assert.Equal(-48, gateway.OutdoorSensor.SignalStrength);
        Assert.Equal("0xA6", gateway.ChannelSensors[0].SensorId);
        Assert.Equal(-69, gateway.ChannelSensors[0].SignalStrength);
    }

    [Fact]
    public void WhenLightningHiddenAfterCreation_ThenNulledOut()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Lightning = new EcowittLightningData { Distance = 15m }
        };
        gateway.UpdateFromLiveData(data);
        Assert.NotNull(gateway.LightningSensor);

        // Act
        gateway.HiddenSensors = ["lightning"];
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Null(gateway.LightningSensor);
    }

    [Fact]
    public void WhenSensorInfoHasIndoorBattery_ThenMapsToIndoorSensor()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Indoor = new EcowittIndoorData { Temperature = 19.3m }
        });

        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0xAB", TypeCode = 4, Rssi = -55, Battery = 0 }
        };

        // Act
        gateway.UpdateSensorInfo(sensorsInfo);

        // Assert
        Assert.Equal("0xAB", gateway.IndoorSensor!.SensorId);
        Assert.Equal(-55, gateway.IndoorSensor.SignalStrength);
        Assert.Equal(1.0m, gateway.IndoorSensor.BatteryLevel); // binary: 0 = OK = full
    }

    [Fact]
    public void WhenWs90SensorInfo_ThenPiezoRainGaugeGetsBattery()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { Temperature = 18.0m },
            PiezoRain = new EcowittRainData { DailyRain = 1.0m }
        });

        var sensorsInfo = new[]
        {
            new EcowittSensorInfo { SensorId = "0x9637", TypeCode = 48, Rssi = -48, Battery = 4 }
        };

        // Act
        gateway.UpdateSensorInfo(sensorsInfo);

        // Assert
        Assert.Equal("0x9637", gateway.PiezoRainGauge!.SensorId);
        Assert.Equal(0.8m, gateway.PiezoRainGauge.BatteryLevel); // 4/5 = 0.8
    }

    [Fact]
    public void WhenChannelHasName_ThenSensorUsesNameAsTitle()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Channels =
            [
                new EcowittChannelData { Channel = 1, Name = "Living Room", Temperature = 21.3m }
            ]
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Equal("Living Room", gateway.ChannelSensors[0].Name);
        Assert.Equal("Living Room", gateway.ChannelSensors[0].Title);
    }

    [Fact]
    public void WhenChannelHasNoName_ThenSensorUsesDefaultTitle()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Channels =
            [
                new EcowittChannelData { Channel = 2, Temperature = 19.0m }
            ]
        };

        // Act
        gateway.UpdateFromLiveData(data);

        // Assert
        Assert.Null(gateway.ChannelSensors[0].Name);
        Assert.Equal("Channel 2", gateway.ChannelSensors[0].Title);
    }

    [Fact]
    public void WhenRainBucketIncreases_ThenConfigChangedIsTrue()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Act — bucket value increased from 50 to 60
        var configChanged = gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 60m }
        });

        // Assert — config should be persisted so lastBucketValue is saved
        Assert.True(configChanged);
        Assert.Equal(60m, gateway.RainLastMonthlyValue);
    }

    [Fact]
    public void WhenRainBucketUnchanged_ThenConfigChangedIsFalse()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Act — same value, no change
        var configChanged = gateway.UpdateFromLiveData(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Assert
        Assert.False(configChanged);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

}
