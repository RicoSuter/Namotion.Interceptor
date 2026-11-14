using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// A subject that has a hosted service, as opposed to a subject that is one. The service runs once the
/// subject is activated, either explicitly with
/// <see cref="InterceptorHostingExtensions.ActivateHostedService"/> or by a hosting context created with
/// <c>activateSubjectHostedServices: true</c>, the default, which activates every such subject as it
/// attaches. In a context that opted out, attaching the subject alone runs nothing.
/// </summary>
public interface ISubjectHostedServiceFactory
{
    /// <summary>
    /// Creates the service that runs this subject. Must construct a new instance on every call,
    /// because a detach disposes the instance and a re-attach asks for a fresh one.
    /// </summary>
    /// <remarks>
    /// The service runs as a factory attachment of the subject, so its stops are not ordered against
    /// the subject's other attachments. A connector it starts for its own run, it owns directly:
    /// docs/hosting.md#a-service-a-hosted-subject-owns.
    /// </remarks>
    /// <param name="serviceProvider">The activator's service provider. Never null; may resolve nothing.</param>
    IHostedService CreateHostedService(IServiceProvider serviceProvider);
}
