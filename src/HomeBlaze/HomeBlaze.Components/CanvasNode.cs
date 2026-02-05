using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A positioned node on a canvas containing a child subject.
/// </summary>
[InterceptorSubject]
public partial class CanvasNode : IConfigurableSubject
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
    /// The child subject to render in this node.
    /// </summary>
    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    public CanvasNode()
    {
        Width = 200;
        Height = 150;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
