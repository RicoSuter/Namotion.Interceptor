using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject without an edit component, so the create dialog and the edit dialog show the generated properties
/// editor for it. The editor has no field for an array, which it therefore shows as text that cannot be applied,
/// and it does not know the range of the level, so that the setter is what rejects a level outside of it.
/// Its only state value is a path to another subject.
/// </summary>
[DisplayName("Plain configurable")]
[InterceptorSubject]
public partial class PlainConfigurableSubject : IConfigurable
{
    public PlainConfigurableSubject()
    {
        Interval = 5;
        Level = 1;
    }

    [Configuration]
    public partial string? Label { get; set; }

    [Configuration]
    public partial int Interval { get; set; }

    [Configuration]
    public partial int? Limit { get; set; }

    [Configuration]
    public partial string[]? Tags { get; set; }

    [Configuration]
    [Range(1, 10)]
    public partial int Level { get; set; }

    /// <summary>
    /// The path of another subject, which the property pane shows as that subject with a link to it.
    /// </summary>
    [State]
    [SubjectPath(typeof(IInterceptorSubject))]
    public partial string? LinkedPath { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
