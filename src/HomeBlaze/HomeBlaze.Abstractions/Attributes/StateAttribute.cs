namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property as part of the subject's visible state in the UI.
/// Only properties marked with this attribute are displayed in the property panel.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
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
    /// Gets or sets the display position of the property.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the value is cumulative (accumulated over time).
    /// </summary>
    public bool IsCumulative { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a discrete variable (binary on/off, every transition matters)
    /// as opposed to an analog variable (continuous values like sensor readings where sampling is acceptable).
    /// </summary>
    public bool IsDiscrete { get; set; }

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