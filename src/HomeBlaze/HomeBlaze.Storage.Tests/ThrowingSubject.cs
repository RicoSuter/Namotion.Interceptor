using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

[InterceptorSubject]
public partial class ThrowingSubject : IConfigurable
{
    public ThrowingSubject()
    {
        throw new InvalidOperationException("Device driver is broken.");
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
