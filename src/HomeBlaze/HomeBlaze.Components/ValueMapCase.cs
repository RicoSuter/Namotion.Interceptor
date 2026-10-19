using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using MudBlazor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One case of a <see cref="ValueMap"/>: what a value with this text gets.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class ValueMapCase : IConfigurable
{
    /// <summary>
    /// The value this case is for, as its raw text, for example "True" or the name of an enum value.
    /// </summary>
    [Configuration]
    public partial string? Value { get; set; }

    /// <summary>
    /// The text shown for the value. Without it the formatted value is shown.
    /// </summary>
    [Configuration]
    public partial string? Text { get; set; }

    /// <summary>
    /// The colour of the value.
    /// </summary>
    [Configuration]
    public partial Color Color { get; set; }

    /// <summary>
    /// Name of the icon shown for the value, for example "Lightbulb".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
