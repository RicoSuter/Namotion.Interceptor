namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property as part of the subject's visible state in the UI.
/// Only properties marked with this attribute are displayed in the property panel.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class StateAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the display name (if not null, overrides the property name).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the unit of the state property for formatting.
    /// </summary>
    public StateUnit Unit { get; set; } = StateUnit.Default;

    /// <summary>
    /// Gets or sets the display order of the property.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the value is cumulative (accumulated over time).
    /// </summary>
    public bool IsCumulative { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this state is a signal (not an imprecise sensor value).
    /// </summary>
    public bool IsSignal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the value is estimated (based on other values).
    /// </summary>
    public bool IsEstimated { get; set; }

    public StateAttribute()
    {
    }

    public StateAttribute(string? name)
    {
        Name = name;
    }
}

/// <summary>
/// Units for state property values.
/// </summary>
public enum StateUnit
{
    Default,
    Percent,
    DegreeCelsius,
    Watt,
    KiloWatt,
    WattHour,
    Volt,
    Ampere,
    Hertz,
    Lumen,
    Lux,
    Meter,
    Millimeter,
    MillimeterPerHour,
    Kilobyte,
    KilobytePerSecond,
    MegabitsPerSecond,
    LiterPerHour,
    Currency,
    HexColor
}
