using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Grid layout with configurable rows and columns.
/// </summary>
[Category("Dashboard")]
[Description("Grid layout with configurable rows and columns")]
[DisplayName("Grid layout")]
[SubjectIcon("GridView")]
[InterceptorSubject]
public partial class GridLayout : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Number of rows in the grid.
    /// </summary>
    [Configuration]
    public partial int Rows { get; set; }

    /// <summary>
    /// Number of columns in the grid.
    /// </summary>
    [Configuration]
    public partial int Columns { get; set; }

    /// <summary>
    /// The width and the height of a cell in the form W:H, for example 1:1 for square cells. With it, a row is as high as
    /// a column is wide times H/W, so the grid follows the width of the page. Without it, the rows share the height.
    /// </summary>
    [Configuration]
    public partial string? CellAspectRatio { get; set; }

    /// <summary>
    /// The cells of the grid. On a page one <c>Cell</c> block each, in a configuration file a list.
    /// </summary>
    [Configuration, State, Slot("Cell")]
    public partial GridCell[] Cells { get; set; }

    public string? Title => "Grid";

    public GridLayout()
    {
        Rows = 2;
        Columns = 2;
        Cells = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
