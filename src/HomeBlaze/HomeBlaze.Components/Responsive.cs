using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows its content only on screens of some widths. The content is always rendered; only CSS hides it.
/// </summary>
[Category("Dashboard")]
[Description("Shows its content only on phones, tablets or desktops")]
[DisplayName("Responsive")]
[SubjectIcon("Devices")]
[InterceptorSubject]
public partial class Responsive : IConfigurable, ITitleProvider
{
    public Responsive()
    {
        Show = ResponsiveShow.NotPhone;
    }

    /// <summary>
    /// The screens that show the content.
    /// </summary>
    [Configuration]
    public partial ResponsiveShow Show { get; set; }

    /// <summary>
    /// The content.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public string? Title => null;

    /// <summary>
    /// Gets the classes of the element that holds the content: <c>responsive-widget</c> and the MudBlazor display classes
    /// that show it only on the chosen screens. A value outside the enum is treated as <see cref="ResponsiveShow.NotPhone"/>.
    /// </summary>
    public static string GetCssClasses(ResponsiveShow show) => show switch
    {
        ResponsiveShow.Phone => "responsive-widget d-block d-sm-none",
        ResponsiveShow.Tablet => "responsive-widget d-none d-sm-block d-lg-none",
        ResponsiveShow.Desktop => "responsive-widget d-none d-lg-block",
        ResponsiveShow.NotDesktop => "responsive-widget d-block d-lg-none",
        _ => "responsive-widget d-none d-sm-block",
    };

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
