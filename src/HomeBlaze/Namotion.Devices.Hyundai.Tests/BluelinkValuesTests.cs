using Namotion.Devices.Hyundai.Mapping;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkValuesTests
{
    [Theory]
    [InlineData(21.0, "0EH")]
    [InlineData(14.0, "00H")]
    [InlineData(31.5, "23H")]
    [InlineData(22.5, "11H")]
    public void WhenTemperatureIsValid_ThenLegacyCodeMatchesThePythonReference(double temperature, string expected)
    {
        // Act
        var code = BluelinkValues.ToTemperatureCode((decimal)temperature);

        // Assert
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(13.5)]
    [InlineData(32.0)]
    [InlineData(21.3)]
    public void WhenTemperatureIsNotInTheTable_ThenItThrows(double temperature)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => BluelinkValues.ValidateTemperature((decimal)temperature));
    }

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(0.8, 80)]
    [InlineData(1.0, 100)]
    public void WhenChargeLimitIsValid_ThenPercentIsReturned(double chargeLimit, int expected)
    {
        // Act
        var percent = BluelinkValues.ToChargeLimitPercent((decimal)chargeLimit, "chargeLimit");

        // Assert
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.85)]
    [InlineData(1.1)]
    public void WhenChargeLimitIsNotASetting_ThenItThrows(double chargeLimit)
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => BluelinkValues.ToChargeLimitPercent((decimal)chargeLimit, "chargeLimit"));
        Assert.Equal("chargeLimit", exception.ParamName);
    }

    [Fact]
    public void WhenVinIsLong_ThenSuffixIsTheLastSixCharacters()
    {
        // Act & Assert
        Assert.Equal("000001", BluelinkValues.VinSuffix("TESTVIN0000000001"));
    }
}
