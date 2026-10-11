using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject without an edit component, so the create dialog shows the generated properties editor for it.
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

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
