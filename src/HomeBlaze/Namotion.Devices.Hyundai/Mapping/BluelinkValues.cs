using System.Globalization;

namespace Namotion.Devices.Hyundai.Mapping;

/// <summary>Validation and conversion of the values that commands send.</summary>
internal static class BluelinkValues
{
    public const decimal MinimumTemperature = 14.0m;
    public const decimal MaximumTemperature = 31.5m;

    /// <summary>Returns the charge limit (0..1) as percent; Bluelink accepts 50 to 100 in steps of 10.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not a setting of the vehicle.</exception>
    public static int ToChargeLimitPercent(decimal chargeLimit, string parameterName)
    {
        var percent = chargeLimit * 100m;
        if (percent is < 50m or > 100m || percent % 10m != 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, chargeLimit, "Bluelink accepts charge limits from 0.5 to 1.0 in steps of 0.1.");
        }

        return (int)percent;
    }

    /// <exception cref="ArgumentOutOfRangeException">The temperature is not in the table of the API.</exception>
    public static void ValidateTemperature(decimal temperature)
    {
        if (temperature is < MinimumTemperature or > MaximumTemperature || temperature * 2m % 1m != 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), temperature, "Bluelink accepts 14.0 to 31.5 °C in steps of 0.5.");
        }
    }

    /// <summary>
    /// The legacy temperature code: the index of the temperature in the table 14.0 to 31.5 °C in steps of 0.5, as
    /// uppercase hex with the suffix H, three characters; 21 °C is <c>0EH</c>.
    /// </summary>
    public static string ToTemperatureCode(decimal temperature)
    {
        ValidateTemperature(temperature);
        var index = (int)((temperature - MinimumTemperature) * 2m);
        return (index.ToString("X", CultureInfo.InvariantCulture) + "H").PadLeft(3, '0');
    }

    /// <summary>Returns the duration of a climate run as whole minutes; Bluelink accepts 1 to 30.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is not a whole number of minutes from 1 to 30.</exception>
    public static int ToClimateMinutes(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromMinutes(30) || duration.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Bluelink accepts climate durations of whole minutes from 1 to 30.");
        }

        return (int)duration.TotalMinutes;
    }

    /// <summary>The last six characters of the VIN, as titles show them.</summary>
    public static string VinSuffix(string vin) => vin.Length <= 6 ? vin : vin[^6..];
}
