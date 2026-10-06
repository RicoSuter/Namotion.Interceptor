using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

[InterceptorSubject]
public partial class ThrowingSubjectWithCause : IConfigurable
{
    public ThrowingSubjectWithCause()
    {
        throw new InvalidOperationException("Could not open port", new IOException("Access denied"));
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
