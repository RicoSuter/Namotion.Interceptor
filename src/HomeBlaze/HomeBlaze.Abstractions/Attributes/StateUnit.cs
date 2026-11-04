namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Units for state property values.
/// </summary>
public enum StateUnit
{
    Default,

    /// <summary>
    /// A fraction from 0 to 1, displayed and entered in the operation dialog as 0 to 100 %.
    /// </summary>
    Percent,

    DegreeCelsius,
    Watt,
    Kilowatt,
    WattHour,
    Volt,
    Ampere,
    Hertz,
    Lumen,
    Lux,
    Meter,
    Kilometer,
    Millimeter,
    MillimeterPerHour,
    Kilobyte,
    KilobytePerSecond,
    MegabitPerSecond,
    LiterPerHour,
    KilowattHour,
    Milliampere,
    Currency,
    HexColor,
    MeterPerSecond,
    Hectopascal,
    Degree,
    UvIndex,
    Byte,
    Kelvin,
    Minute,
    Hour,
    VoltAmpere,
    VoltAmpereReactive,
    VoltAmpereHour,
    VoltAmpereReactiveHour,
    AmpereHour,
    WattPerSquareMeter,

    /// <summary>
    /// A power level in decibels relative to one milliwatt, such as the received signal strength of a radio.
    /// </summary>
    DecibelMilliwatt
}
