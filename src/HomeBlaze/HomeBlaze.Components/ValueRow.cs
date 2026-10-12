using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One row of a <see cref="ValueList"/>: an icon, a label, optional content and a value.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class ValueRow : IConfigurable
{
    /// <summary>
    /// Path of the property whose value the row shows. Optional when the row has content.
    /// </summary>
    [Configuration]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the row. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name of the icon at the start of the row, for example "Bolt".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that says what the value gets.
    /// </summary>
    [Configuration]
    public partial string? Map { get; set; }

    /// <summary>
    /// Content shown between the label and the value.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
