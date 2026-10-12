using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A titled card that wraps page content.
/// </summary>
[Category("Dashboard")]
[Description("A titled card that wraps page content")]
[DisplayName("Card")]
[SubjectIcon("WebAsset")]
[PageLayout]
[InterceptorSubject]
public partial class Card : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Title shown at the top of the card.
    /// </summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>
    /// Name of the icon shown next to the title, for example "Thermostat".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// The body of the card.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    /// <summary>
    /// Content shown below a divider at the bottom of the card.
    /// </summary>
    [Slot]
    public partial IInterceptorSubject? Footer { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
