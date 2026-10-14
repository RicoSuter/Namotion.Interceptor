using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows a state as a coloured chip or icon.
/// </summary>
[Category("Dashboard")]
[Description("Shows a state as a coloured chip or icon")]
[DisplayName("Indicator")]
[SubjectIcon("Lightbulb")]
[InterceptorSubject]
public partial class Indicator : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the property whose value the indicator shows.
    /// </summary>
    [Configuration]
    [SubjectPath]
    public partial string? Path { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that says what the value gets.
    /// Without a map, a boolean that is true has the primary colour.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    /// <summary>
    /// Name of the icon, for example "Lightbulb". The icon of a matched case of the map replaces it.
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Whether the indicator is a chip with text or an icon alone.
    /// </summary>
    [Configuration]
    public partial IndicatorKind Kind { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
