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
    private readonly Lazy<ISubjectDiscovery[]> _subjectDiscoveries;

    public SubjectDiscoveryRegistry(
        TypeProvider typeProvider,
        SubjectComponentRegistry componentRegistry,
        IServiceProvider serviceProvider,
        ILogger<SubjectDiscoveryRegistry> logger)
    {
        _componentRegistry = componentRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _subjectDiscoveries = new Lazy<ISubjectDiscovery[]>(() => CreateSubjectDiscoveries(typeProvider.Types));
    }

    /// <summary>
    /// Gets one instance of every non-abstract <see cref="ISubjectDiscovery"/> type whose subject type has a Setup or
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>, at most one per subject type. A
    /// subject discovery that cannot be created, has no such component, or finds a subject type that a subject
    /// discovery earlier in the ordinal order of the full type names already finds, is left out and logged as an error.
    /// </summary>
    /// <remarks>
    /// The subject discoveries are created once, on the first call, from the types loaded then; types added later are
    /// not scanned, so every subject discovery type must be loaded before.
    /// </remarks>
    public IReadOnlyList<ISubjectDiscovery> GetSubjectDiscoveries() => _subjectDiscoveries.Value;

    private ISubjectDiscovery[] CreateSubjectDiscoveries(IReadOnlyCollection<Type> types)
    {
        // Ordered, so that which of two subject discoveries for one subject type is used does not depend on the load order.
        var discoveryTypes = types
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition && typeof(ISubjectDiscovery).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        var discoveries = new List<ISubjectDiscovery>();
        var discoveriesBySubjectType = new Dictionary<Type, Type>();
        foreach (var type in discoveryTypes)
        {
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

                // The Discover page tells results apart by subject type and key, which is unique within one subject discovery.
                if (!discoveriesBySubjectType.TryAdd(discovery.SubjectType, type))
                {
                    _logger.LogError(
                        "The subject discovery {Discovery} is not used: {SubjectType} already has the subject discovery {UsedDiscovery}.",
                        type, discovery.SubjectType, discoveriesBySubjectType[discovery.SubjectType]);
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
