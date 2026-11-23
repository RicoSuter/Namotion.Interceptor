using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject that <see cref="DiscoverableTestDiscovery"/> finds a device for.
/// </summary>
[DisplayName("Discoverable test device")]
[InterceptorSubject]
public partial class DiscoverableTestSubject : IConfigurable
{
    [Configuration]
    public partial string? HostAddress { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
