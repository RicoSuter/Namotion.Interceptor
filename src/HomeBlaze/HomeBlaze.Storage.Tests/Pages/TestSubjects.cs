using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests.Pages;

[InterceptorSubject]
public partial class TestLeaf : IConfigurable
{
    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial int Count { get; set; }

    [Configuration]
    public partial bool Enabled { get; set; }

    [Configuration]
    public partial TimeSpan Interval { get; set; }

    [Configuration]
    public partial List<string>? Tags { get; set; }

    public int ApplyCount { get; private set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ApplyCount++;
        return Task.CompletedTask;
    }
}
