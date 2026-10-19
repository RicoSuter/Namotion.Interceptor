using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A positioned node on a canvas containing a child subject.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class CanvasNode : IConfigurable
{
    /// <summary>
    /// X position in pixels from left edge.
    /// </summary>
    [Configuration]
    public partial int X { get; set; }

    /// <summary>
    /// Y position in pixels from top edge.
    /// </summary>
    [Configuration]
    public partial int Y { get; set; }

    /// <summary>
    /// Width in pixels.
    /// </summary>
    [Configuration]
    public partial int Width { get; set; }

    /// <summary>
    /// Height in pixels.
    /// </summary>
    [Configuration]
    public partial int Height { get; set; }

    /// <summary>
    /// What the node shows. On a page the content of the <c>Node</c> block, in a configuration file one subject.
    /// </summary>
    [Configuration, Slot(IsDefault = true)]
    public partial IInterceptorSubject? Child { get; set; }

    public CanvasNode()
    {
        Width = 200;
        Height = 150;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
