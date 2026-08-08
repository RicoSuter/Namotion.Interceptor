using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// The constructor shape docs/subject-guidelines.md teaches: it takes the context and never uses it,
/// so the generator emits no context constructor and only an unconditional attach saves it.
/// </summary>
[InterceptorSubject]
public partial class SubjectIgnoringContextParameter : IHostedService
{
    private int _startCount;

    public SubjectIgnoringContextParameter(IInterceptorSubjectContext? context = null)
    {
    }

    public partial string? Name { get; set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
