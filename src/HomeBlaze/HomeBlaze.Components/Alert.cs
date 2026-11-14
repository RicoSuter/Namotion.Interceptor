using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A highlighted box for a note or a warning.
/// </summary>
[Category("Dashboard")]
[Description("A highlighted box for a note or a warning")]
[DisplayName("Alert")]
[SubjectIcon("Info")]
[PageLayout]
[InterceptorSubject]
public partial class Alert : IConfigurable, ITitleProvider
{
    public Alert()
    {
        Severity = MudBlazor.Severity.Info;
        Variant = MudBlazor.Variant.Text;
    }

    /// <summary>
    /// How serious the note is, which decides its colour and its icon.
    /// </summary>
    [Configuration]
    public partial MudBlazor.Severity Severity { get; set; }

    /// <summary>
    /// Whether the box is tinted, outlined or filled.
    /// </summary>
    [Configuration]
    public partial MudBlazor.Variant Variant { get; set; }

    /// <summary>
    /// Name of an icon that replaces the icon of the severity, for example "Bolt".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// The text of the box.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
