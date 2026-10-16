using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
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
    [SubjectPath(Kinds = SubjectPathKinds.Boolean)]
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
    [SubjectPath(Kinds = SubjectPathKinds.Operation)]
    public partial string? On { get; set; }

    /// <summary>
    /// Path of the operation that turns the switch off. Set together with <see cref="On"/>.
    /// </summary>
    [Configuration]
    [SubjectPath(Kinds = SubjectPathKinds.Operation)]
    public partial string? Off { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
