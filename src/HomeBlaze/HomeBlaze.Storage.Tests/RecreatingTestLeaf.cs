using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

[InterceptorSubject]
public partial class RecreatingTestLeaf : IConfigurable
{
    public RecreatingTestLeaf()
    {
        Name = string.Empty;
    }

    [Configuration]
    public partial string Name { get; set; }

    [Configuration(RecreateOnChange = true)]
    public partial string? Shape { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
