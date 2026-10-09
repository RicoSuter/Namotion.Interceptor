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
    /// The service must not detach an attachment it made on its subject from its own stop path or the
    /// unwinding of its <c>ExecuteAsync</c>: that attachment's stop waits for the service's stop, so the
    /// detach waits on itself. See docs/hosting.md#do-not-detach-from-your-own-stop-path.
    /// </remarks>
    /// <param name="serviceProvider">The activator's service provider. Never null; may resolve nothing.</param>
    IHostedService CreateHostedService(IServiceProvider serviceProvider);
}
