using System.ComponentModel;
using System.Numerics;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Sets a number by moving a thumb between a minimum and a maximum.
/// </summary>
[Category("Dashboard")]
[Description("Sets a number between a minimum and a maximum")]
[DisplayName("Slider")]
[SubjectIcon("Tune")]
[InterceptorSubject]
public partial class Slider : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the number property that the slider shows and writes.
    /// </summary>
    [Configuration]
    [SubjectPath(ValueTypes = [typeof(INumber<>)], IsWritable = true)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label above the slider. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// The value at the left end, in the unit of the property. Defaults to the Minimum attribute of the property,
    /// then to 0 for a percent.
    /// </summary>
    [Configuration]
    public partial double? Minimum { get; set; }

    /// <summary>
    /// The value at the right end, in the unit of the property. Defaults to the Maximum attribute of the property,
    /// then to 1 for a percent.
    /// </summary>
    [Configuration]
    public partial double? Maximum { get; set; }

    /// <summary>
    /// The distance between two positions, in the unit of the property. Defaults to 1 for a property that holds whole
    /// numbers and to a hundredth of the range otherwise.
    /// </summary>
    [Configuration]
    public partial double? Step { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
