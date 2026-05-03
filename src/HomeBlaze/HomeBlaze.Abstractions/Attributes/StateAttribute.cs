namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property as part of the subject's visible state in the UI.
/// Only properties marked with this attribute are displayed in the property panel.
/// Multiple [State] attributes (from class and interfaces) are merged by PropertyAttributeInitializer.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class StateAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the display title (if not null, overrides the property name in the UI).
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the unit of the state property for formatting.
    /// </summary>
    public StateUnit Unit
    {
        get;
        set
        {
            field = value;
            IsUnitSet = true;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the value is cumulative (accumulated over time).
    /// </summary>
    public bool IsCumulative
    {
        get;
        set
        {
            field = value;
            IsCumulativeSet = true;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether this is a discrete variable.
    /// </summary>
    public bool IsDiscrete
    {
        get;
        set
        {
            field = value;
            IsDiscreteSet = true;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the value is estimated.
    /// </summary>
    public bool IsEstimated
    {
        get;
        set
        {
            field = value;
            IsEstimatedSet = true;
        }
    }

    /// <summary>
    /// Gets or sets the display position of the property.
    /// </summary>
    public int Position
    {
        get;
        set
        {
            field = value;
            IsPositionSet = true;
        }
    }

    // Tracking properties — used by PropertyAttributeInitializer merge logic
    // NOT intended as named attribute parameters
    public bool IsUnitSet { get; private set; }
    public bool IsCumulativeSet { get; private set; }
    public bool IsDiscreteSet { get; private set; }
    public bool IsEstimatedSet { get; private set; }
    public bool IsPositionSet { get; private set; }

    public StateAttribute()
    {
    }

    public StateAttribute(string? title)
    {
        Title = title;
    }
}
