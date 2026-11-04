using HomeBlaze.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Extension methods for registering the HomeBlaze history UI in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <see cref="IPropertyHistoryViewer"/> through which value widgets show the history of their property.
    /// </summary>
    public static IServiceCollection AddPropertyHistoryViewer(this IServiceCollection services)
    {
        // Scoped, because the dialog service belongs to the circuit.
        services.AddScoped<IPropertyHistoryViewer, PropertyHistoryViewer>();
        return services;
    }
}
