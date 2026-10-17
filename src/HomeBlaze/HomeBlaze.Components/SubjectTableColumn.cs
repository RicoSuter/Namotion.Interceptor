using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One column of a <see cref="SubjectTable"/>: a property that every row shows.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class SubjectTableColumn : IConfigurable
{
    /// <summary>
    /// Name of the property of the row subject that the column shows, for example "CurrentSpeed". It is no path.
    /// </summary>
    [Configuration]
    public partial string? Property { get; set; }

    /// <summary>
    /// The header of the column. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that says what the value gets.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
