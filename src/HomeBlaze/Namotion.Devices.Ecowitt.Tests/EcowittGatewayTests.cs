using System.Text.Json;
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
        gateway.Update(data);

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
        gateway.Update(data);

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
        gateway.Update(data);

        // Assert
        Assert.NotNull(gateway.RainGauge);
        Assert.Equal(0.0m, gateway.RainGauge!.RainRate);
        Assert.Equal(3.8m, gateway.RainGauge.DailyRain);
        Assert.Equal(56.5m, gateway.RainGauge.YearlyRain);
    }

    [Fact]
    public void WhenGw2000RainParsed_ThenGaugeExposesEachBucket()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = EcowittClient.ParseLiveData(JsonSerializer.Deserialize<JsonElement>(EcowittClientTests.Gw2000RainJson));

        // Act
        gateway.Update(data);

        // Assert
        var gauge = gateway.RainGauge!;
        Assert.Equal(0.3m, gauge.HourlyRain);
        Assert.Equal(2.4m, gauge.Last24HoursRain);
        Assert.Equal(0.9m, gauge.DailyRain);
        Assert.Equal(14.8m, gauge.WeeklyRain);
        Assert.Equal(20.5m, gauge.MonthlyRain);
        Assert.Equal(506.1m, gauge.YearlyRain);
        Assert.Equal(506.1m, gauge.TotalRain);
    }

    [Fact]
    public void WhenStatePersistedBeforeIdRemap_ThenTotalRainDoesNotJump()
    {
        // Arrange
        // The previous mapping read 0x13 as MonthlyRain and, without 0x14, accumulated it.
        var gateway = CreateGateway();
        gateway.RainCumulativeOffset = 120m;
        gateway.RainLastMonthlyValue = 506.1m;
        gateway.PiezoRainCumulativeOffset = 40m;
        gateway.PiezoRainLastMonthlyValue = 294.9m;
        var data = EcowittClient.ParseLiveData(JsonSerializer.Deserialize<JsonElement>(EcowittClientTests.Gw2000RainJson));

        // Act
        var configChanged = gateway.Update(data);

        // Assert
        Assert.False(configChanged);
        Assert.Equal(626.1m, gateway.RainGauge!.TotalRain);
        Assert.Equal(120m, gateway.RainCumulativeOffset);
        Assert.Equal(334.9m, gateway.PiezoRainGauge!.TotalRain);
        Assert.Equal(40m, gateway.PiezoRainCumulativeOffset);
    }

    [Fact]
    public void WhenGatewayReportsTotalRain_ThenTotalRainAccumulatesFromItInsteadOfYearly()
    {
        // Arrange
        // The previous mapping read 0x14 as YearlyRain and accumulated it.
        var gateway = CreateGateway();
        gateway.RainLastMonthlyValue = 1834.7m;

        // Act
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { MonthlyRain = 20.5m, YearlyRain = 506.1m, TotalRain = 1840.2m }
        });

        // Assert
        Assert.Equal(1840.2m, gateway.RainGauge!.TotalRain);
        Assert.Equal(0m, gateway.RainCumulativeOffset);
        Assert.Equal(1840.2m, gateway.RainLastMonthlyValue);
    }

    [Fact]
    public void WhenYearlyRainMissingForOnePoll_ThenShorterBucketIsNotCountedAsReset()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.RainCumulativeOffset = 120m;
        gateway.RainLastMonthlyValue = 506.1m;

        // Act
        var configChanged = gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { DailyRain = 0.9m, WeeklyRain = 14.8m, MonthlyRain = 20.5m }
        });

        // Assert
        Assert.False(configChanged);
        Assert.Null(gateway.RainGauge!.TotalRain);
        Assert.Equal(120m, gateway.RainCumulativeOffset);
        Assert.Equal(506.1m, gateway.RainLastMonthlyValue);
    }

    [Fact]
    public void WhenYearlyRainIncreases_ThenTotalRainAccumulates()
    {
        // Arrange
        var gateway = CreateGateway();

        // Act — first poll: yearly rain is 50mm
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Assert
        Assert.Equal(50m, gateway.RainGauge!.TotalRain);

        // Act — second poll: yearly rain increased to 60mm
        gateway.Update(new EcowittLiveData
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
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 150m }
        });
        Assert.Equal(150m, gateway.RainGauge!.TotalRain);

        // Act — year rolls over, yearly counter resets to 5mm
        gateway.Update(new EcowittLiveData
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
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 100m }
        });

        // Act — first reset
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 20m }
        });

        // Act — second reset
        gateway.Update(new EcowittLiveData
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
        gateway.Update(data);

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
        gateway.Update(data1);
        var originalSensor = gateway.ChannelSensors[0];

        var data2 = new EcowittLiveData
        {
            Channels = [new EcowittChannelData { Channel = 1, Temperature = 22.0m }]
        };

        // Act
        gateway.Update(data2);

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
        gateway.Update(data1);
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
        gateway.Update(data2);

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
        gateway.Update(data);

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
        gateway.Update(data);

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
        gateway.Update(data1);
        var original = gateway.OutdoorSensor;

        var data2 = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { Temperature = 19.0m }
        };

        // Act
        gateway.Update(data2);

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
                StrikeCount = 3
            }
        };

        // Act
        gateway.Update(data);

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
        gateway.Update(data);
        Assert.NotNull(gateway.RainGauge);

        // Act
        gateway.HiddenSensors = ["rain"];
        gateway.Update(data);

        // Assert
        Assert.Null(gateway.RainGauge);
    }

    [Fact]
    public void WhenOutdoorAndIndoorHiddenAfterCreation_ThenNulledOut()
    {
        // Arrange
        var gateway = CreateGateway();
        var data = new EcowittLiveData
        {
            Outdoor = new EcowittOutdoorData { Temperature = 18.0m },
            Indoor = new EcowittIndoorData { Temperature = 22.5m }
        };
        gateway.Update(data);
        Assert.NotNull(gateway.OutdoorSensor);
        Assert.NotNull(gateway.IndoorSensor);

        // Act
        gateway.HiddenSensors = ["outdoor", "indoor"];
        gateway.Update(data);

        // Assert
        Assert.Null(gateway.OutdoorSensor);
        Assert.Null(gateway.IndoorSensor);
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
        gateway.Update(data);
        Assert.NotNull(gateway.LightningSensor);

        // Act
        gateway.HiddenSensors = ["lightning"];
        gateway.Update(data);

        // Assert
        Assert.Null(gateway.LightningSensor);
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
        gateway.Update(data);

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
        gateway.Update(data);

        // Assert
        Assert.Null(gateway.ChannelSensors[0].Name);
        Assert.Equal("Channel 2", gateway.ChannelSensors[0].Title);
    }

    [Fact]
    public void WhenRainBucketIncreases_ThenConfigChangedIsTrue()
    {
        // Arrange
        var gateway = CreateGateway();
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Act — bucket value increased from 50 to 60
        var configChanged = gateway.Update(new EcowittLiveData
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
        gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Act — same value, no change
        var configChanged = gateway.Update(new EcowittLiveData
        {
            Rain = new EcowittRainData { YearlyRain = 50m }
        });

        // Assert
        Assert.False(configChanged);
    }
}
