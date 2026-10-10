using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One tab of a <see cref="TabLayout"/>.
/// </summary>
[InterceptorSubject]
public partial class Tab : IConfigurable
{
    /// <summary>
    /// The label of the tab.
    /// </summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>
    /// Name of the icon shown on the tab, for example "Bolt".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// What the tab shows when it is selected.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
