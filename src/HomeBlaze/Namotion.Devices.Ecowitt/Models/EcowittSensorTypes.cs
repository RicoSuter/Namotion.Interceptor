namespace Namotion.Devices.Ecowitt.Models;

/// <summary>
/// The readings of the gateway's live data that a paired sensor can feed.
/// </summary>
internal enum EcowittReading
{
    Outdoor,
    Indoor,
    Rain,
    PiezoRain,
    Lightning,
    Channel,
    SoilMoisture,
    LeafWetness,
    Temperature,
    Pm25,
    Leak
}

/// <summary>
/// Maps the type codes of the gateway's sensor list (see Ecowitt.md) to sensor models and to the readings they feed.
/// </summary>
internal static class EcowittSensorTypes
{
    /// <summary>
    /// Gets the sensor model of a type code, for a type code that is not mapped the image key the gateway reports
    /// for the slot in upper case, or null without one.
    /// </summary>
    public static string? GetTypeName(int typeCode, string? imageKey) => typeCode switch
    {
        0 => "WH69",
        1 => "WH68",
        2 => "WH80",
        3 => "WH40",
        4 => "WH25",
        5 => "WH26",
        >= 6 and <= 13 => "WH31",
        >= 14 and <= 21 or >= 58 and <= 65 => "WH51",
        >= 22 and <= 25 => "WH41",
        26 => "WH57",
        >= 27 and <= 30 => "WH55",
        >= 31 and <= 38 => "WH34",
        39 => "WH45",
        >= 40 and <= 47 => "WH35",
        // The gateway reports the image key wh90 for the WS90.
        48 => "WS90",
        // The gateway reports the image key wh85 for the WS85.
        49 => "WS85",
        >= 66 and <= 69 => "WH54",
        70 => "WN20",
        71 => "WN38",
        _ => imageKey?.ToUpperInvariant()
    };

    /// <summary>
    /// Gets whether the type reports its battery as 0 (OK) or 1 (low) instead of a 0 to 5 level.
    /// </summary>
    public static bool HasBinaryBattery(int typeCode) => typeCode is >= 4 and <= 13;

    /// <summary>
    /// Gets whether a sensor of the type feeds a single-instance reading that the gateway merges from all such sensors.
    /// </summary>
    public static bool Feeds(int typeCode, EcowittReading reading) => reading switch
    {
        EcowittReading.Outdoor => typeCode is 0 or 1 or 2 or 5 or 48 or 49,
        EcowittReading.Indoor => typeCode is 4,
        EcowittReading.Rain => typeCode is 0 or 3,
        EcowittReading.PiezoRain => typeCode is 48 or 49,
        EcowittReading.Lightning => typeCode is 26,
        _ => false
    };

    /// <summary>
    /// Gets the per-channel reading and channel that a sensor of the type feeds, false for other types.
    /// </summary>
    public static bool TryGetChannelSlot(int typeCode, out EcowittReading reading, out int channel)
    {
        (reading, channel) = typeCode switch
        {
            >= 6 and <= 13 => (EcowittReading.Channel, typeCode - 5),
            >= 14 and <= 21 => (EcowittReading.SoilMoisture, typeCode - 13),
            >= 22 and <= 25 => (EcowittReading.Pm25, typeCode - 21),
            >= 27 and <= 30 => (EcowittReading.Leak, typeCode - 26),
            >= 31 and <= 38 => (EcowittReading.Temperature, typeCode - 30),
            >= 40 and <= 47 => (EcowittReading.LeafWetness, typeCode - 39),
            >= 58 and <= 65 => (EcowittReading.SoilMoisture, typeCode - 49),
            _ => (default, 0)
        };

        return channel != 0;
    }
}
