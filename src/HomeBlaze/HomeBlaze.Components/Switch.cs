using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Turns a boolean on and off, by writing the property or by invoking an operation per state.
/// </summary>
[Category("Dashboard")]
[Description("Turns a boolean on and off")]
[DisplayName("Switch")]
[SubjectIcon("ToggleOn")]
[InterceptorSubject]
public partial class Switch : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the boolean property that the switch shows, and writes when <see cref="On"/> and <see cref="Off"/> are not set.
    /// </summary>
    [Configuration]
    [SubjectPath(ValueTypes = [typeof(bool)])]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label next to the switch. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Path of the operation that turns the switch on. Set together with <see cref="Off"/>.
    /// </summary>
    [Configuration]
    [SubjectPath(Kind = SubjectPathKind.Operation)]
    public partial string? On { get; set; }

    /// <summary>
    /// Path of the operation that turns the switch off. Set together with <see cref="On"/>.
    /// </summary>
    [Configuration]
    [SubjectPath(Kind = SubjectPathKind.Operation)]
    public partial string? Off { get; set; }

    /// <summary>
    /// Whether the switch is a toggle switch or a tile.
    /// </summary>
    [Configuration]
    public partial SwitchKind Kind { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that gives a state its color, icon and text. A tile is filled with the
    /// color of the matched case, a toggle switch takes it as its color. Without a map, true has the primary color.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    /// <summary>
    /// A second, smaller line below the label of a tile. Without it, a tile with a map shows the text of the value.
    /// </summary>
    [Configuration]
    public partial string? Subtitle { get; set; }

    /// <summary>
    /// Name of the icon of a tile, for example "Lightbulb". The icon of a matched case of the map replaces it.
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Content drawn over the top right corner of a tile, such as an indicator. A click on it reaches the tile.
    /// </summary>
    [Slot]
    public partial IInterceptorSubject? Badge { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
