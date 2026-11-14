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
[PageLayout]
[InterceptorSubject]
public partial class ValueTile : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the property whose value the tile shows, or with a <see cref="Map"/> of a subject whose states the cases of the map read.
    /// </summary>
    [Configuration]
    [SubjectPath]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the tile. Defaults to the title the property declares, then to its name, and to the path when the path leads nowhere. It can show values with expressions between double braces, such as <c>{{ subject/Name }}</c>.
    /// </summary>
    [Configuration]
    [TextTemplate]
    public partial string? Label { get; set; }

    /// <summary>
    /// A second, smaller line below the label, for what the tile stands for, such as the device that the value comes from. It never replaces the text of a matched case of the <see cref="Map"/>, which is the value. It can show values with expressions between double braces, such as <c>{{ subject/Name }}</c>.
    /// </summary>
    [Configuration]
    [TextTemplate]
    public partial string? Subtitle { get; set; }

    /// <summary>
    /// Whether a tile has a thin border like an outlined card, whatever its fill.
    /// </summary>
    [Configuration]
    public partial bool Outlined { get; set; }

    /// <summary>
    /// Name of the icon, for example "Bolt", placed as <see cref="Kind"/> says. The icon of a matched case of the map
    /// replaces it.
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that says what the value gets.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    /// <summary>
    /// Whether the value is drawn as a box or as a tile.
    /// </summary>
    [Configuration]
    public partial ValueTileKind Kind { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
