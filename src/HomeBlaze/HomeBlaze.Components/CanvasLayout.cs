using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Free-form canvas layout with draggable nodes at arbitrary positions.
/// </summary>
[Category("Layouts")]
[Description("Free-form canvas layout with draggable widgets")]
[InterceptorSubject]
public partial class CanvasLayout : IConfigurableSubject, ITitleProvider
{
    /// <summary>
    /// Optional minimum height in pixels. Defaults to 400.
    /// </summary>
    [Configuration]
    public partial int? MinHeight { get; set; }

    /// <summary>
    /// Enable snapping node positions to grid.
    /// </summary>
    [Configuration]
    public partial bool IsSnapToGridEnabled { get; set; }

    /// <summary>
    /// Grid size in pixels for snap-to-grid. Default: 100.
    /// </summary>
    [Configuration]
    public partial int GridSize { get; set; }

    /// <summary>
    /// Collection of nodes positioned on the canvas.
    /// </summary>
    [Configuration]
    public partial List<CanvasNode> Nodes { get; set; }

    public string? Title => "Canvas";

    public CanvasLayout()
    {
        GridSize = 100;
        Nodes = new List<CanvasNode>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
