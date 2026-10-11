using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows one of several tabs at a time.
/// </summary>
[Category("Dashboard")]
[Description("Shows page content in tabs")]
[DisplayName("Tab layout")]
[SubjectIcon("Tab")]
[InterceptorSubject]
public partial class TabLayout : IConfigurable, ITitleProvider
{
    public TabLayout()
    {
        Tabs = [];
    }

    /// <summary>
    /// The tabs, one per <c>Tab</c> block.
    /// </summary>
    [Slot("Tab")]
    public partial Tab[] Tabs { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
