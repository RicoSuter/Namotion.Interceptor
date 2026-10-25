// The widget that the documentation builds in "A Complete Widget" (Docs/development/building-subjects.md).
// It is compiled here so that the documentation shows code that builds, and so that the page samples
// which use it are loaded by DocumentationSamplesTests. That test compares everything from the first
// using directive on with the documentation, so change both together.
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace MyCompany.Widgets;

/// <summary>
/// The sidebar of a <see cref="SidebarLayout"/>.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class Sidebar : IConfigurable
{
    public Sidebar()
    {
        Width = 240;
    }

    /// <summary>
    /// The width of the sidebar in pixels.
    /// </summary>
    [Configuration]
    public partial int Width { get; set; }

    /// <summary>
    /// What the sidebar shows.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Shows page content next to a sidebar.
/// </summary>
[Category("Dashboard")]
[Description("Shows page content next to a sidebar")]
[InterceptorSubject]
public partial class SidebarLayout : IConfigurable
{
    /// <summary>
    /// The main content.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    /// <summary>
    /// The sidebar, created from the <c>Sidebar</c> block.
    /// </summary>
    [Slot]
    public partial Sidebar? Sidebar { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
