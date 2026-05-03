using Namotion.Devices.Ecowitt.Models;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittValueParserTests
{
    // ParseDecimal

    [Fact]
    public void WhenDecimalValueIsPlainNumber_ThenReturnsDecimal()
    {
        // Act
        var result = EcowittValueParser.ParseDecimal("99");

        // Assert
        Assert.Equal(99m, result);
    }

    [Fact]
    public void WhenDecimalValueIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseDecimal(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenDecimalValueIsEmpty_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseDecimal("");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenDecimalValueIsDisconnectedSensor_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseDecimal("--.-");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenDecimalValueIsDoubleDash_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseDecimal("--");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenDecimalValueHasUnitSuffix_ThenStripsUnit()
    {
        // Act
        var result = EcowittValueParser.ParseDecimal("1.8 m/s");

        // Assert
        Assert.Equal(1.8m, result);
    }

    // ParseTemperature

    [Fact]
    public void WhenTemperatureIsCelsius_ThenReturnsAsIs()
    {
        // Act
        var result = EcowittValueParser.ParseTemperature("18.1", "℃");

        // Assert
        Assert.Equal(18.1m, result);
    }

    [Fact]
    public void WhenTemperatureIsFahrenheit_ThenConvertsToCelsius()
    {
        // Act
        var result = EcowittValueParser.ParseTemperature("64.6", "℉");

        // Assert
        Assert.Equal(18.1m, result);
    }

    [Fact]
    public void WhenTemperatureIsFWithLetterF_ThenConvertsToCelsius()
    {
        // Act
        var result = EcowittValueParser.ParseTemperature("32.0", "F");

        // Assert
        Assert.Equal(0.0m, result);
    }

    [Fact]
    public void WhenTemperatureIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseTemperature(null, "℃");

        // Assert
        Assert.Null(result);
    }

    // ParseHumidity

    [Fact]
    public void WhenHumidityHasPercentSign_ThenReturnsNormalized()
    {
        // Act
        var result = EcowittValueParser.ParseHumidity("44%");

        // Assert
        Assert.Equal(0.44m, result);
    }

    [Fact]
    public void WhenHumidityIs100Percent_ThenReturnsOne()
    {
        // Act
        var result = EcowittValueParser.ParseHumidity("100%");

        // Assert
        Assert.Equal(1.00m, result);
    }

    [Fact]
    public void WhenHumidityIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseHumidity(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenHumidityIsDisconnected_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseHumidity("--%");

        // Assert
        Assert.Null(result);
    }

    // ParseWindSpeed

    [Fact]
    public void WhenWindSpeedInMetersPerSecond_ThenReturnsAsIs()
    {
        // Act
        var result = EcowittValueParser.ParseWindSpeed("1.8 m/s");

        // Assert
        Assert.Equal(1.8m, result);
    }

    [Fact]
    public void WhenWindSpeedInMph_ThenConvertsToMs()
    {
        // Act
        var result = EcowittValueParser.ParseWindSpeed("4.03 mph");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 1.80m, 1.81m);
    }

    [Fact]
    public void WhenWindSpeedInKmh_ThenConvertsToMs()
    {
        // Act
        var result = EcowittValueParser.ParseWindSpeed("6.5 km/h");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 1.80m, 1.81m);
    }

    [Fact]
    public void WhenWindSpeedInKnots_ThenConvertsToMs()
    {
        // Act
        var result = EcowittValueParser.ParseWindSpeed("3.5 knots");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 1.80m, 1.81m);
    }

    [Fact]
    public void WhenWindSpeedIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseWindSpeed(null);

        // Assert
        Assert.Null(result);
    }

    // ParsePressure

    [Fact]
    public void WhenPressureInHpa_ThenReturnsAsIs()
    {
        // Act
        var result = EcowittValueParser.ParsePressure("946.2 hPa");

        // Assert
        Assert.Equal(946.2m, result);
    }

    [Fact]
    public void WhenPressureInInHg_ThenConvertsToHpa()
    {
        // Act
        var result = EcowittValueParser.ParsePressure("27.94 inHg");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 946.0m, 947.0m);
    }

    [Fact]
    public void WhenPressureInMmHg_ThenConvertsToHpa()
    {
        // Act
        var result = EcowittValueParser.ParsePressure("710.0 mmHg");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 946.0m, 947.0m);
    }

    [Fact]
    public void WhenPressureIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParsePressure(null);

        // Assert
        Assert.Null(result);
    }

    // ParseRain

    [Fact]
    public void WhenRainInMm_ThenReturnsAsIs()
    {
        // Act
        var result = EcowittValueParser.ParseRain("3.8 mm");

        // Assert
        Assert.Equal(3.8m, result);
    }

    [Fact]
    public void WhenRainInInches_ThenConvertsToMm()
    {
        // Act
        var result = EcowittValueParser.ParseRain("0.15 in");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 3.8m, 3.9m);
    }

    [Fact]
    public void WhenRainIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseRain(null);

        // Assert
        Assert.Null(result);
    }

    // ParseRainRate

    [Fact]
    public void WhenRainRateInMmPerHour_ThenReturnsAsIs()
    {
        // Act
        var result = EcowittValueParser.ParseRainRate("0.0 mm/Hr");

        // Assert
        Assert.Equal(0.0m, result);
    }

    [Fact]
    public void WhenRainRateInInchesPerHour_ThenConvertsToMm()
    {
        // Act
        var result = EcowittValueParser.ParseRainRate("1.0 in/Hr");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(25.4m, result);
    }

    [Fact]
    public void WhenRainRateIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseRainRate(null);

        // Assert
        Assert.Null(result);
    }

    // ParseIlluminance

    [Fact]
    public void WhenIlluminanceInKlux_ThenConvertsToLux()
    {
        // Act
        var result = EcowittValueParser.ParseIlluminance("0.50 Klux");

        // Assert
        Assert.Equal(500m, result);
    }

    [Fact]
    public void WhenIlluminanceInKluxZero_ThenReturnsZero()
    {
        // Act
        var result = EcowittValueParser.ParseIlluminance("0.00 Klux");

        // Assert
        Assert.Equal(0m, result);
    }

    [Fact]
    public void WhenIlluminanceInWm2_ThenConvertsToLux()
    {
        // Act
        var result = EcowittValueParser.ParseIlluminance("100.0 W/m²");

        // Assert
        Assert.NotNull(result);
        Assert.InRange(result.Value, 12000m, 13000m);
    }

    [Fact]
    public void WhenIlluminanceIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.ParseIlluminance(null);

        // Assert
        Assert.Null(result);
    }

    // NormalizeBatteryLevel

    [Fact]
    public void WhenBinaryBatteryIsZero_ThenReturnsFull()
    {
        // Act
        var result = EcowittValueParser.NormalizeBatteryLevel(0, isBinaryBattery: true);

        // Assert
        Assert.Equal(1.0m, result);
    }

    [Fact]
    public void WhenBinaryBatteryIsOne_ThenReturnsEmpty()
    {
        // Act
        var result = EcowittValueParser.NormalizeBatteryLevel(1, isBinaryBattery: true);

        // Assert
        Assert.Equal(0.0m, result);
    }

    [Fact]
    public void WhenScaleBatteryIsFive_ThenReturnsFull()
    {
        // Act
        var result = EcowittValueParser.NormalizeBatteryLevel(5, isBinaryBattery: false);

        // Assert
        Assert.Equal(1.0m, result);
    }

    [Fact]
    public void WhenScaleBatteryIsThree_ThenReturnsSixtyPercent()
    {
        // Act
        var result = EcowittValueParser.NormalizeBatteryLevel(3, isBinaryBattery: false);

        // Assert
        Assert.Equal(0.6m, result);
    }

    [Fact]
    public void WhenBatteryIsNine_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.NormalizeBatteryLevel(9, isBinaryBattery: false);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenBatteryIsNull_ThenReturnsNull()
    {
        // Act
        var result = EcowittValueParser.NormalizeBatteryLevel(null, isBinaryBattery: false);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenBatteryIsSixWithBinaryMode_ThenReturnsFull()
    {
        // Act — value 6 = DC powered
        var result = EcowittValueParser.NormalizeBatteryLevel(6, isBinaryBattery: true);

        // Assert
        Assert.Equal(1.0m, result);
    }

    [Fact]
    public void WhenBatteryIsSixWithLevelMode_ThenReturnsFull()
    {
        // Act — value 6 = DC powered
        var result = EcowittValueParser.NormalizeBatteryLevel(6, isBinaryBattery: false);

        // Assert
        Assert.Equal(1.0m, result);
    }
}
