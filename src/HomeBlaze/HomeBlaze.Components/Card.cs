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
[InterceptorSubject]
public partial class Card : IConfigurable, ITitleProvider
{
    public Card()
    {
        Outlined = true;
        Elevation = 2;
    }

    /// <summary>
    /// Title shown at the top of the card.
    /// </summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>
    /// Smaller line shown under the title.
    /// </summary>
    [Configuration]
    public partial string? Subtitle { get; set; }

    /// <summary>
    /// Name of the icon shown next to the title, for example "Thermostat".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Colour of the icon.
    /// </summary>
    [Configuration]
    public partial MudBlazor.Color IconColor { get; set; }

    /// <summary>
    /// Where the title leads to. Written like a link in the text of a page: relative to the page,
    /// a route of the application, or a web address. It has no effect on a card without a title.
    /// </summary>
    [Configuration]
    public partial string? Href { get; set; }

    /// <summary>
    /// Whether the card has a border instead of a shadow. This is the default, and <c>false</c> gives a card with a shadow, see <see cref="Elevation"/>.
    /// </summary>
    [Configuration]
    public partial bool Outlined { get; set; }

    /// <summary>
    /// Depth of the shadow of a card that is not outlined, from 0 for none. An outlined card has no shadow.
    /// </summary>
    [Configuration]
    public partial int Elevation { get; set; }

    /// <summary>
    /// Address of a picture shown between the header and the body. Written like <see cref="Href"/>.
    /// </summary>
    [Configuration]
    public partial string? Image { get; set; }

    /// <summary>
    /// Height of the whole card in pixels, above 0; 0 or less is a widget argument error. Without it the card is as high as
    /// its content. Wins over <see cref="AspectRatio"/>.
    /// </summary>
    [Configuration]
    public partial int? Height { get; set; }

    /// <summary>
    /// Width and height in the form <c>W:H</c>, for example <c>4:3</c>, so that the height of the card follows its width.
    /// </summary>
    [Configuration]
    public partial string? AspectRatio { get; set; }

    /// <summary>
    /// The body of the card.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    /// <summary>
    /// Content shown at the right end of the header.
    /// </summary>
    [Slot]
    public partial IInterceptorSubject? HeaderRight { get; set; }

    /// <summary>
    /// Content shown below a divider at the bottom of the card.
    /// </summary>
    [Slot]
    public partial IInterceptorSubject? Footer { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
