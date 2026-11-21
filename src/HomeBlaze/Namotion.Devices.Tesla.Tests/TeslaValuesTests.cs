using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaValuesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("<invalid>", null)]
    [InlineData("  <invalid> ", null)]
    [InlineData("Spotify", "Spotify")]
    public void WhenTextIsNormalized_ThenPlaceholdersBecomeNull(string? value, string? expected)
    {
        // Act
        var result = TeslaValues.Text(value);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenMilesAreConverted_ThenKilometersAreReturned()
    {
        // Act
        var result = TeslaValues.MilesToKilometers(36469.004009m);

        // Assert
        Assert.Equal(58691.17m, Math.Round(result!.Value, 2));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    public void WhenFloatIsNotRepresentableAsDecimal_ThenNullIsReturned(float value)
    {
        // Act
        var result = TeslaValues.Decimal(value);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenFloatIsConverted_ThenDecimalWithoutFloatNoiseIsReturned()
    {
        // Act & Assert
        Assert.Equal(230.45m, TeslaValues.Decimal(230.45f));
        Assert.Equal(-1.5m, TeslaValues.Decimal(-1.5f));
        Assert.Equal(0m, TeslaValues.Decimal(0f));
    }

    [Fact]
    public void WhenPercentIsConverted_ThenFractionIsReturned()
    {
        // Act & Assert
        Assert.Equal(0.7m, TeslaValues.Percent(70));
        Assert.Null(TeslaValues.Percent(null));
    }

    [Fact]
    public void WhenUnixTimesAreConverted_ThenDateTimeOffsetIsReturned()
    {
        // Act & Assert
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795316400), TeslaValues.FromUnixSeconds(1795316400));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795293677353), TeslaValues.FromUnixMilliseconds(1795293677353));
        Assert.Null(TeslaValues.FromUnixSeconds(0));
    }

    [Fact]
    public void WhenMinutesAfterMidnightAreConverted_ThenTimeSpanIsReturned()
    {
        // Act & Assert
        Assert.Equal(TimeSpan.FromHours(4), TeslaValues.MinutesAfterMidnight(240));
    }

    [Theory]
    [InlineData("Complete", TeslaChargingState.Complete)]
    [InlineData("NoPower", TeslaChargingState.NoPower)]
    [InlineData("something new", TeslaChargingState.Unknown)]
    [InlineData(null, null)]
    public void WhenEnumIsParsed_ThenKnownValuesMapAndUnknownValuesBecomeUnknown(string? value, TeslaChargingState? expected)
    {
        // Act
        var result = TeslaValues.Enum<TeslaChargingState>(value, NullLogger.Instance);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("99")]
    public void WhenNumericTextIsParsedAsEnum_ThenItBecomesUnknown(string value)
    {
        // Act
        var result = TeslaValues.Enum<TeslaChargingState>(value, NullLogger.Instance);

        // Assert
        Assert.Equal(TeslaChargingState.Unknown, result);
    }

    [Theory]
    [InlineData("P", TeslaShiftState.Park)]
    [InlineData("D", TeslaShiftState.Drive)]
    [InlineData("X", TeslaShiftState.Unknown)]
    [InlineData(null, null)]
    public void WhenShiftStateIsParsed_ThenLettersMapToGears(string? value, TeslaShiftState? expected)
    {
        // Act
        var result = TeslaValues.ShiftState(value);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenOlderSectionTimestampArrives_ThenClockRejectsIt()
    {
        // Arrange
        var clock = new TeslaSectionClock();

        // Act
        var first = clock.TryAdvance(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var older = clock.TryAdvance(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var same = clock.TryAdvance(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var missing = clock.TryAdvance(null);

        // Assert
        Assert.True(first);
        Assert.False(older);
        Assert.True(same);
        Assert.True(missing);
    }

    [Fact]
    public void WhenVehicleDataIsBuilt_ThenBase64RoundTripsTheSnapshot()
    {
        // Arrange
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1795293677);

        // Act
        var base64 = VehicleDataBuilder.ToBase64(VehicleDataBuilder.Create(timestamp));
        var parsed = CarServer.VehicleData.Parser.ParseFrom(Convert.FromBase64String(base64));

        // Assert
        Assert.Equal(65, parsed.ChargeState.BatteryLevel);
        Assert.Equal(CarServer.ChargeState.Types.ChargingState.TypeOneofCase.Charging, parsed.ChargeState.ChargingState.TypeCase);
        Assert.True(parsed.ClosuresState.HasLocked);
        Assert.Equal(timestamp, parsed.ClosuresState.Timestamp.ToDateTimeOffset());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1.0, null)]
    [InlineData(0.0, 0.0)]
    [InlineData(12.5, 12.5)]
    public void WhenMinutesAreConverted_ThenNegativeAndMissingBecomeNull(double? minutes, double? expectedMinutes)
    {
        // Act
        var result = TeslaValues.Minutes((decimal?)minutes);

        // Assert
        Assert.Equal(expectedMinutes is null ? null : TimeSpan.FromMinutes(expectedMinutes.Value), result);
    }

    [Fact]
    public void WhenCoordinateIsMissingAPart_ThenNullIsReturned()
    {
        // Act & Assert
        Assert.Null(TeslaValues.Coordinate(47m, null));
        Assert.Null(TeslaValues.Coordinate(null, 8m));
        Assert.Equal(new HomeBlaze.Abstractions.Common.GeoCoordinate(47.0, 8.0), TeslaValues.Coordinate(47m, 8m));
    }
}
