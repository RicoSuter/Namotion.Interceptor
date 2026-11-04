using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One panel of <see cref="ExpansionPanels"/>.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class ExpansionPanel : IConfigurable
{
    /// <summary>
    /// The title of the panel, which stays visible when the panel is closed.
    /// </summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>
    /// Name of the icon shown next to the title, for example "Bed".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Whether the panel is open when the page loads.
    /// </summary>
    [Configuration]
    public partial bool Expanded { get; set; }

    /// <summary>
    /// What the panel shows when it is open.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
