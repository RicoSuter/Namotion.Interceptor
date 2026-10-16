using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows a number as a bar between a minimum and a maximum.
/// </summary>
[Category("Dashboard")]
[Description("Shows a number as a bar between a minimum and a maximum")]
[DisplayName("Progress")]
[SubjectIcon("LinearScale")]
[InterceptorSubject]
public partial class Progress : IConfigurable, ITitleProvider
{
    public Progress()
    {
        Maximum = 1;
        Color = MudBlazor.Color.Primary;
    }

    /// <summary>
    /// Path of the property whose value the bar shows.
    /// </summary>
    [Configuration]
    [SubjectPath(Kinds = SubjectPathKinds.Number)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The value at which the bar is empty.
    /// </summary>
    [Configuration]
    public partial double Minimum { get; set; }

    /// <summary>
    /// The value at which the bar is full.
    /// </summary>
    [Configuration]
    public partial double Maximum { get; set; }

    /// <summary>
    /// The colour of the bar.
    /// </summary>
    [Configuration]
    public partial MudBlazor.Color Color { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
