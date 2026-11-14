using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Virtual;

/// <summary>How a virtual property is shown as state; the fields of <see cref="StateAttribute"/>.</summary>
public sealed record VirtualState
{
    /// <summary>The title shown instead of the name.</summary>
    [Configuration]
    public string? Title { get; init; }

    /// <summary>The unit of the value.</summary>
    [Configuration]
    public StateUnit? Unit { get; init; }

    /// <summary>The position among the properties of the subject.</summary>
    [Configuration]
    public int? Position { get; init; }

    /// <summary>The category the property is grouped under.</summary>
    [Configuration]
    public string? Category { get; init; }

    /// <summary>Whether the value is a cumulative counter.</summary>
    [Configuration]
    public bool? IsCumulative { get; init; }

    /// <summary>Whether the value is discrete.</summary>
    [Configuration]
    public bool? IsDiscrete { get; init; }

    /// <summary>Whether the value is estimated.</summary>
    [Configuration]
    public bool? IsEstimated { get; init; }
}
