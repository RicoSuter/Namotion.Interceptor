using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject without an edit component, so the create dialog and the edit dialog show the generated properties
/// editor for it. The editor has no field for an array, which it therefore shows as text that cannot be applied.
/// </summary>
[DisplayName("Plain configurable")]
[InterceptorSubject]
public partial class PlainConfigurableSubject : IConfigurable
{
    public PlainConfigurableSubject()
    {
        Interval = 5;
    }

    [Configuration]
    public partial string? Label { get; set; }

    [Configuration]
    public partial int Interval { get; set; }

    [Configuration]
    public partial int? Limit { get; set; }

    [Configuration]
    public partial string[]? Tags { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
