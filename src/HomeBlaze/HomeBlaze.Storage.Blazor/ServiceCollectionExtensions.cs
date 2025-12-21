using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Blazor;

/// <summary>
/// Extension methods for registering HomeBlaze.Storage services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze storage services to the service collection.
    /// </summary>
    public static IServiceCollection AddHomeBlazeStorage(this IServiceCollection services)
    {
        services.AddSingleton<MarkdownContentParser>();
        services.AddScoped<ISubjectCreator, BlazorSubjectCreator>();
        return services;
    }
}
