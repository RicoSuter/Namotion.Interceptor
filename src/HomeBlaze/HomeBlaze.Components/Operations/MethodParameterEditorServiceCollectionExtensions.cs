using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Components.Operations;

/// <summary>
/// Registers the editors of operation parameters, see <see cref="MethodParameterEditor"/>.
/// </summary>
public static class MethodParameterEditorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TEditor"/> as the editor named <paramref name="name"/>; the last registration of a
    /// name wins.
    /// </summary>
    /// <param name="services">The services of the application.</param>
    /// <param name="name">The name that <see cref="HomeBlaze.Abstractions.Attributes.OperationParameterAttribute.Editor"/> gives.</param>
    public static IServiceCollection AddMethodParameterEditor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TEditor>(this IServiceCollection services, string name)
        where TEditor : MethodParameterEditor
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddSingleton(new MethodParameterEditorRegistration(name, typeof(TEditor)));
        return services;
    }
}
