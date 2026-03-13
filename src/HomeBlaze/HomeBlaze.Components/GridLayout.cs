using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Grid layout with configurable rows and columns.
/// </summary>
[Category("Layouts")]
[Description("Grid layout with configurable rows and columns")]
[InterceptorSubject]
public partial class GridLayout : IConfigurableSubject, ITitleProvider
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
    /// Collection of cells in the grid.
    /// </summary>
    [Configuration, State]
    public partial List<GridCell> Cells { get; set; }

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
