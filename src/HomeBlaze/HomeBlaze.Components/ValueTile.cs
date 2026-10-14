using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A tile with a small label and one large value.
/// </summary>
[Category("Dashboard")]
[Description("A tile with a small label and one large value")]
[DisplayName("Value tile")]
[SubjectIcon("Pin")]
[InterceptorSubject]
public partial class ValueTile : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the property whose value the tile shows.
    /// </summary>
    [Configuration]
    [SubjectPath]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the tile. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name of the icon next to the label, for example "Bolt".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that says what the value gets.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
