using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Picks one value of an enum from a list.
/// </summary>
[Category("Dashboard")]
[Description("Picks one value of an enum")]
[DisplayName("Select")]
[SubjectIcon("ArrowDropDownCircle")]
[InterceptorSubject]
public partial class Select : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the enum property that the select shows and writes.
    /// </summary>
    [Configuration]
    [SubjectPath(ValueTypes = [typeof(Enum)], IsWritable = true)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the select. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that gives each value its text and icon.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
