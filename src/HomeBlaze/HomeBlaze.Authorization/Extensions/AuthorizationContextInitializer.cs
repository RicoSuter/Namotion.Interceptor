using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;

namespace HomeBlaze.Authorization.Extensions;

/// <summary>
/// Hosted service that initializes the authorization interceptor on the subject context.
/// This runs after the service provider is fully built, allowing us to register the
/// service provider on the context for interceptor DI access.
/// </summary>
internal sealed class AuthorizationContextInitializer : IHostedService
{
    private readonly IInterceptorSubjectContext _context;
    private readonly IServiceProvider _serviceProvider;

    public AuthorizationContextInitializer(
        IInterceptorSubjectContext context,
        IServiceProvider serviceProvider)
    {
        _context = context;
        _serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Register service provider on context for interceptors that need DI access
        _context.AddService(_serviceProvider);

        // Add authorization interceptor
        _context.WithAuthorization();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}