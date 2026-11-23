using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Services.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Creates the subject discoveries among the loaded types that the create dialog can use.
/// </summary>
public sealed class SubjectDiscoveryRegistry
{
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubjectDiscoveryRegistry> _logger;
    private readonly Lazy<ISubjectDiscovery[]> _discoveries;

    public SubjectDiscoveryRegistry(
        TypeProvider typeProvider,
        SubjectComponentRegistry componentRegistry,
        IServiceProvider serviceProvider,
        ILogger<SubjectDiscoveryRegistry> logger)
    {
        _componentRegistry = componentRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _discoveries = new Lazy<ISubjectDiscovery[]>(() => CreateDiscoveries(typeProvider.Types));
    }

    /// <summary>
    /// Gets one instance of every non-abstract <see cref="ISubjectDiscovery"/> type whose subject type has a Setup or
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>. A subject discovery that cannot be
    /// created, or has no such component, is left out and logged as an error.
    /// </summary>
    /// <remarks>
    /// The subject discoveries are created once, on the first call, from the types loaded then; types added later are
    /// not scanned, so every subject discovery type must be loaded before.
    /// </remarks>
    public IReadOnlyList<ISubjectDiscovery> GetDiscoveries() => _discoveries.Value;

    private ISubjectDiscovery[] CreateDiscoveries(IReadOnlyCollection<Type> types)
    {
        var discoveries = new List<ISubjectDiscovery>();
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(ISubjectDiscovery).IsAssignableFrom(type))
            {
                continue;
            }

            try
            {
                var discovery = (ISubjectDiscovery)ActivatorUtilities.CreateInstance(_serviceProvider, type);
                if (!AcceptsDiscoveryResults(discovery.SubjectType))
                {
                    _logger.LogError(
                        "The subject discovery {Discovery} is not used: {SubjectType} has no Setup or Edit component that implements {Interface}.",
                        type, discovery.SubjectType, nameof(ISubjectDiscoveryComponent));
                    continue;
                }

                discoveries.Add(discovery);
            }
            catch (Exception exception)
            {
                // A broken plugin type, such as a TypeLoadException, must not take the other subject discoveries down.
                _logger.LogError(exception, "The subject discovery {Discovery} could not be created.", type);
            }
        }

        return discoveries.ToArray();
    }

    private bool AcceptsDiscoveryResults(Type subjectType)
    {
        var registration = _componentRegistry.GetEditorComponent(subjectType, isCreating: true);

        return registration is { TakesDiscoveryResults: true };
    }
}
