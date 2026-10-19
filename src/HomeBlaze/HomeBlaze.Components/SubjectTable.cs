using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A table with one row per subject of a folder or of a property that holds subjects, and one column per property.
/// </summary>
[Category("Dashboard")]
[Description("A table with a row per subject and a column per property")]
[DisplayName("Subject table")]
[SubjectIcon("TableChart")]
[PageLayout]
[InterceptorSubject]
public partial class SubjectTable : IConfigurable, ITitleProvider
{
    public SubjectTable()
    {
        Columns = [];
    }

    /// <summary>
    /// Path of a subject whose folder holds the rows, or of a property that holds them.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(IInterceptorSubject))]
    public partial string? Source { get; set; }

    /// <summary>
    /// Full name of a class, base class or interface. Only rows of that type are shown; without it every subject is a row.
    /// </summary>
    [Configuration]
    public partial string? Type { get; set; }

    /// <summary>
    /// The columns of the table, the first one links to the row subject. On a page one <c>Column</c> block each, in a
    /// configuration file a list.
    /// </summary>
    [Configuration, Slot("Column")]
    public partial SubjectTableColumn[] Columns { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
