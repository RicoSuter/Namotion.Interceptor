using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A cell in a grid layout containing a child subject.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class GridCell : IConfigurable
{
    /// <summary>
    /// Row position (0-indexed). Null for auto-flow.
    /// </summary>
    [Configuration]
    public partial int? Row { get; set; }

    /// <summary>
    /// Column position (0-indexed). Null for auto-flow.
    /// </summary>
    [Configuration]
    public partial int? Column { get; set; }

    /// <summary>
    /// Number of rows this cell spans. Default: 1.
    /// </summary>
    [Configuration]
    public partial int RowSpan { get; set; }

    /// <summary>
    /// Number of columns this cell spans. Default: 1.
    /// </summary>
    [Configuration]
    public partial int ColumnSpan { get; set; }

    /// <summary>
    /// What the cell shows. On a page the content of the <c>Cell</c> block, in a configuration file one subject.
    /// </summary>
    [Configuration, Slot(IsDefault = true)]
    public partial IInterceptorSubject? Child { get; set; }

    public GridCell()
    {
        RowSpan = 1;
        ColumnSpan = 1;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
