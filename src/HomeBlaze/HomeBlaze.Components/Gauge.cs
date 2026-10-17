using System.ComponentModel;
using System.Numerics;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows a number as a ring between a minimum and a maximum, with the value in its center.
/// </summary>
[Category("Dashboard")]
[Description("Shows a number as a ring with the value in its center")]
[DisplayName("Gauge")]
[SubjectIcon("Speed")]
[InterceptorSubject]
public partial class Gauge : IConfigurable, ITitleProvider
{
    public Gauge()
    {
        Size = GaugeSize.Medium;
    }

    /// <summary>
    /// Path of the property whose value the gauge shows.
    /// </summary>
    [Configuration]
    [SubjectPath(ValueTypes = [typeof(INumber<>)])]
    public partial string? Path { get; set; }

    /// <summary>
    /// The text below the ring. Defaults to the title the property declares, then to its name, and to the path when the path leads nowhere.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// The value at which the ring is empty. Without it the range comes from the property, see <see cref="ValueRangeResolver"/>.
    /// </summary>
    [Configuration]
    public partial double? Minimum { get; set; }

    /// <summary>
    /// The value at which the ring is full. Without it the range comes from the property, see <see cref="ValueRangeResolver"/>.
    /// </summary>
    [Configuration]
    public partial double? Maximum { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that gives the text in the center and the color of the ring.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    /// <summary>
    /// The size of the ring.
    /// </summary>
    [Configuration]
    public partial GaugeSize Size { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
