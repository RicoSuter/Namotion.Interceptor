using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows the control that fits its target: a button, a switch, an indicator, a select, a slider or a value row.
/// </summary>
[Category("Dashboard")]
[Description("Shows the control that fits a property or an operation")]
[DisplayName("Control")]
[SubjectIcon("SettingsRemote")]
[InterceptorSubject]
public partial class Control : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the property or the operation that the control is for.
    /// </summary>
    [Configuration]
    [SubjectPath(Kinds = SubjectPathKinds.Value | SubjectPathKinds.Operation)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the control. Defaults to the title of the property or of the method, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// The value at the left end of a slider, in the unit of the property.
    /// </summary>
    [Configuration]
    public partial double? Minimum { get; set; }

    /// <summary>
    /// The value at the right end of a slider, in the unit of the property.
    /// </summary>
    [Configuration]
    public partial double? Maximum { get; set; }

    /// <summary>
    /// The distance between two positions of a slider, in the unit of the property.
    /// </summary>
    [Configuration]
    public partial double? Step { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> for a select, an indicator or a value row.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
