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
    public OperationButton()
    {
        Color = MudBlazor.Color.Primary;
        Variant = MudBlazor.Variant.Filled;
    }

    /// <summary>
    /// Path of the operation or query that the button invokes.
    /// </summary>
    [Configuration]
    [SubjectPath(Kind = SubjectPathKinds.Operation)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The text of the button. Defaults to the title of the method.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name of the icon at the start of the button, for example "Stop". Defaults to the icon of the method.
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// The colour of the button.
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
