using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject with an operation that fails the way a device call fails when its request times out.
/// </summary>
[InterceptorSubject]
public partial class FailingOperationSubject
{
    /// <summary>
    /// Throws the <see cref="TaskCanceledException"/> with which an HTTP client reports its timeout.
    /// </summary>
    [Operation(Title = "Time Out")]
    public void TimeOut() => throw new TaskCanceledException("The request timed out.");
}
