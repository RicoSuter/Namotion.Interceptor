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
    /// Where the tab bar is. At a side, it moves to the top while the window is too narrow for it.
    /// </summary>
    [Configuration]
    public partial TabPosition Position { get; set; }

    /// <summary>
    /// The tabs, one per <c>Tab</c> block.
    /// </summary>
    [Slot("Tab")]
    public partial Tab[] Tabs { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
