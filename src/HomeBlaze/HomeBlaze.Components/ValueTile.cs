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
    /// Path of the property whose value the tile shows.
    /// </summary>
    [Configuration]
    [SubjectPath]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the tile. Defaults to the title the property declares, then to its name, and to the path when the path leads nowhere.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

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
