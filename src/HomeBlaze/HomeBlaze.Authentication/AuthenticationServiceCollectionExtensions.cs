using HomeBlaze.Authentication.Data;
using HomeBlaze.Authentication.Internal;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Authentication;

/// <summary>Registers the authentication database, Identity, the role and user stores and their startup.</summary>
public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the authentication database (a scoped context and a context factory over the same options), Identity's user
    /// and role managers and sign-in manager over it, the <see cref="RoleStore"/> as <see cref="IRoleStore"/>, the
    /// <see cref="UserStore"/> as <see cref="IMcpClientAccess"/>, and the hosted <see cref="AuthenticationStartup"/>.
    /// Requires the HomeBlaze services, the <see cref="AuthorizationService"/> and Data Protection.
    /// </summary>
    public static IServiceCollection AddHomeBlazeAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AuthenticationOptions>(configuration.GetSection(AuthenticationOptions.SectionName));
        services.AddAuthenticationDatabase();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuthorizationProjection>();
        services.AddSingleton<AuthorizationChangeGate>();
        services.AddSingleton(serviceProvider => new AuthenticationEventLog(serviceProvider.GetRequiredService<ILogger<AuthenticationEventLog>>()));
        // The user callbacks resolve the user store when called, as the user store depends on the role store.
        services.AddSingleton(serviceProvider => new RoleStore(
            serviceProvider.GetRequiredService<IDbContextFactory<AuthenticationDbContext>>(),
            serviceProvider.GetRequiredService<AuthorizationService>(),
            serviceProvider.GetServices<IRoleNameReferences>(),
            () => serviceProvider.GetRequiredService<UserStore>().GetAllRoleSets(),
            () => serviceProvider.GetRequiredService<UserStore>().GetPersonRoleSets(),
            serviceProvider.GetRequiredService<AuthorizationProjection>(),
            serviceProvider.GetRequiredService<AuthorizationChangeGate>())
        {
            EventLog = serviceProvider.GetRequiredService<AuthenticationEventLog>()
        });
        services.AddSingleton<IRoleStore>(serviceProvider => serviceProvider.GetRequiredService<RoleStore>());
        services.AddSingleton(serviceProvider => new UserStore(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<AuthorizationService>(),
            serviceProvider.GetRequiredService<RoleStore>(),
            serviceProvider.GetRequiredService<AuthorizationProjection>(),
            serviceProvider.GetRequiredService<AuthorizationChangeGate>(),
            serviceProvider.GetRequiredService<TimeProvider>())
        {
            EventLog = serviceProvider.GetRequiredService<AuthenticationEventLog>()
        });
        services.AddSingleton<IMcpClientAccess>(serviceProvider => serviceProvider.GetRequiredService<UserStore>());
        services.AddSingleton(serviceProvider => new AuthorizationSubjectBinding(
            serviceProvider.GetRequiredService<IInterceptorSubjectContext>(),
            serviceProvider.GetRequiredService<AuthorizationProjection>(),
            serviceProvider.GetRequiredService<ILogger<AuthorizationSubjectBinding>>()));
        services.AddSingleton<IHostedService>(serviceProvider => new AuthenticationStartup(
            serviceProvider.GetRequiredService<RootManager>(),
            serviceProvider.GetRequiredService<AuthenticationDatabase>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<RoleStore>(),
            serviceProvider.GetRequiredService<UserStore>(),
            serviceProvider.GetRequiredService<AuthorizationSubjectBinding>(),
            serviceProvider.GetRequiredService<ILogger<AuthenticationStartup>>()));
        return services;
    }
}
