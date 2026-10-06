using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

[InterceptorSubject]
public partial class CountingConfigurableSubject : IConfigurable
{
    private int _applyCount;

    public CountingConfigurableSubject()
    {
        Value = string.Empty;
    }

    [Configuration]
    public partial string Value { get; set; }

    public int ApplyCount => Volatile.Read(ref _applyCount);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _applyCount);
        return Task.CompletedTask;
    }
}
