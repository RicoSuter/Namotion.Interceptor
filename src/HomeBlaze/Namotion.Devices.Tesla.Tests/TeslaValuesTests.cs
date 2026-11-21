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
}
