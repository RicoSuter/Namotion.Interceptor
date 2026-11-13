using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A list of rows that each show a label and a value.
/// </summary>
[Category("Dashboard")]
[Description("A list of rows with a label and a value each")]
[DisplayName("Value list")]
[SubjectIcon("List")]
[PageLayout]
[InterceptorSubject]
public partial class ValueList : IConfigurable, ITitleProvider
{
    public ValueList()
    {
        Values = [];
    }

    /// <summary>
    /// The rows, one per <c>Value</c> block.
    /// </summary>
    [Slot("Value")]
    public partial ValueRow[] Values { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
