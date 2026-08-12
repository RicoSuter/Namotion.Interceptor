using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// The host and context bootstrap the hosting tests share. Tests that wire something extra in
/// before the host is built, a deferrer or a second context, call <see cref="CreateContext"/> and
/// build the host themselves.
/// </summary>
internal static class HostingTestHost
{
    /// <summary>
    /// Creates the context under test, with hosting wired into <paramref name="builder"/>'s services.
    /// </summary>
    /// <remarks>
    /// WithContextInheritance, not just WithLifecycle: without it a child subject's Context never
    /// resolves the handler and every child scenario is silently unreachable.
    /// </remarks>
    public static IInterceptorSubjectContext CreateContext(HostApplicationBuilder builder)
        => InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

    /// <summary>
    /// Starts a host over a fresh context. The caller stops the host, which tests that stop it
    /// mid-scenario need.
    /// </summary>
    public static async Task<(IHost Host, IInterceptorSubjectContext Context)> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();
        return (host, context);
    }

    /// <summary>
    /// Runs <paramref name="action"/> against a started host and stops the host afterwards.
    /// </summary>
    public static async Task RunAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var (host, context) = await StartAsync();
        try
        {
            await action(context);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
