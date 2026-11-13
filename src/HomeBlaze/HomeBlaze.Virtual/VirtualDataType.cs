namespace HomeBlaze.Virtual;

/// <summary>The type of the value of a virtual property.</summary>
public enum VirtualDataType
{
    /// <summary>A <see cref="decimal"/>.</summary>
    Number,

    /// <summary>A <see cref="bool"/>.</summary>
    Boolean,

    /// <summary>A <see cref="string"/>.</summary>
    Text,

    /// <summary>A <see cref="System.TimeSpan"/>.</summary>
    Duration,

    /// <summary>A <see cref="System.DateTimeOffset"/>.</summary>
    DateTime
}
