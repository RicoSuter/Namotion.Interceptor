using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Lays its items out in equal columns that wrap to as many rows as the width needs.
/// </summary>
[Category("Dashboard")]
[Description("Lays items out in columns that wrap by screen width")]
[DisplayName("Flow layout")]
[SubjectIcon("ViewColumn")]
[PageLayout]
[InterceptorSubject]
public partial class FlowLayout : IConfigurable, ITitleProvider
{
    public FlowLayout()
    {
        MinWidth = 320;
        Items = [];
    }

    /// <summary>
    /// The narrowest a column may get, in pixels, before the items wrap.
    /// </summary>
    [Configuration]
    public partial int MinWidth { get; set; }

    /// <summary>
    /// The cells of the layout, one per <c>Item</c> block.
    /// </summary>
    [Slot("Item")]
    public partial IInterceptorSubject[] Items { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
