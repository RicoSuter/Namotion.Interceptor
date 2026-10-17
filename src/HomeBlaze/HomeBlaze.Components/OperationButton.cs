using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A button that invokes an operation or a query.
/// </summary>
[Category("Dashboard")]
[Description("A button that invokes an operation or a query")]
[DisplayName("Operation button")]
[SubjectIcon("SmartButton")]
[InterceptorSubject]
public partial class OperationButton : IConfigurable, ITitleProvider
{
    /// <summary>The color of a button that sets none.</summary>
    internal const MudBlazor.Color DefaultColor = MudBlazor.Color.Primary;

    /// <summary>The variant of a button that sets none.</summary>
    internal const MudBlazor.Variant DefaultVariant = MudBlazor.Variant.Filled;

    public OperationButton()
    {
        Color = DefaultColor;
        Variant = DefaultVariant;
    }

    /// <summary>
    /// Path of the operation or query that the button invokes.
    /// </summary>
    [Configuration]
    [SubjectPath(Kind = SubjectPathKind.Operation)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The text of the button. Defaults to the title of the method.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name of the icon at the start of the button, for example "Stop". Defaults to the icon of the method,
    /// else "PlayArrow" for an operation and "Search" for a query.
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// The color of the button.
    /// </summary>
    [Configuration]
    public partial MudBlazor.Color Color { get; set; }

    /// <summary>
    /// Whether the button is filled, outlined or text only.
    /// </summary>
    [Configuration]
    public partial MudBlazor.Variant Variant { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
