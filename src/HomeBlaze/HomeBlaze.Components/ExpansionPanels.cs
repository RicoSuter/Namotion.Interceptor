using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows page content in panels that open and close.
/// </summary>
[Category("Dashboard")]
[Description("Shows page content in panels that open and close")]
[DisplayName("Expansion panels")]
[SubjectIcon("ExpandMore")]
[InterceptorSubject]
public partial class ExpansionPanels : IConfigurable, ITitleProvider
{
    public ExpansionPanels()
    {
        Panels = [];
    }

    /// <summary>
    /// Whether opening a panel closes the others.
    /// </summary>
    [Configuration]
    public partial bool Single { get; set; }

    /// <summary>
    /// The panels, one per <c>Panel</c> block.
    /// </summary>
    [Slot("Panel")]
    public partial ExpansionPanel[] Panels { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
