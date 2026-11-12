using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Metadata;
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
        services.AddSingleton<IMetadataNotesFormat, MetadataNotesFormat>();
        services.AddScoped<ISubjectSetupService, BlazorSubjectSetupService>();
        return services;
    }
}
