using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Free-form canvas layout with draggable nodes at arbitrary positions.
/// </summary>
[Category("Dashboard")]
[Description("Free-form canvas layout with draggable widgets")]
[DisplayName("Canvas layout")]
[SubjectIcon("Dashboard")]
[InterceptorSubject]
public partial class CanvasLayout : IConfigurable, ITitleProvider
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
    /// How the canvas is sized on the page. With <see cref="CanvasScale.FitWidth"/> a viewed canvas fills the width of
    /// its place, smaller or larger than designed, and keeps its proportions.
    /// </summary>
    [Configuration]
    public partial CanvasScale Scale { get; set; }

    /// <summary>
    /// The width in pixels the canvas was designed for. Defaults to the right edge of its rightmost node.
    /// </summary>
    [Configuration]
    public partial int? Width { get; set; }

    /// <summary>
    /// The nodes on the canvas. On a page one <c>Node</c> block each, in a configuration file a list.
    /// </summary>
    [Configuration, State, Slot("Node")]
    public partial CanvasNode[] Nodes { get; set; }

    public string? Title => "Canvas";

    public CanvasLayout()
    {
        GridSize = 100;
        Nodes = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
