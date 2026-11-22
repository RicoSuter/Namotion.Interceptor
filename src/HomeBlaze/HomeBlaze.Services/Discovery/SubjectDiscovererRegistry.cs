using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Services.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Creates the subject discoverers among the loaded types that the create dialog can use.
/// </summary>
public sealed class SubjectDiscovererRegistry
{
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubjectDiscovererRegistry> _logger;
    private readonly Lazy<ISubjectDiscoverer[]> _discoverers;

    public SubjectDiscovererRegistry(
        TypeProvider typeProvider,
        SubjectComponentRegistry componentRegistry,
        IServiceProvider serviceProvider,
        ILogger<SubjectDiscovererRegistry> logger)
    {
        _componentRegistry = componentRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _discoverers = new Lazy<ISubjectDiscoverer[]>(() => CreateDiscoverers(typeProvider.Types));
    }

    /// <summary>
    /// Gets one instance of every non-abstract <see cref="ISubjectDiscoverer"/> type whose subject type has a Setup or
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>, at most one per subject type. A
    /// discoverer that cannot be created, has no such component, or finds a subject type that a discoverer earlier in
    /// the ordinal order of the full type names already finds, is left out and logged as an error.
    /// </summary>
    /// <remarks>
    /// The discoverers are created once, on the first call, from the types loaded then; types added later are not
    /// scanned, so every discoverer type must be loaded before.
    /// </remarks>
    public IReadOnlyList<ISubjectDiscoverer> GetDiscoverers() => _discoverers.Value;

    private ISubjectDiscoverer[] CreateDiscoverers(IReadOnlyCollection<Type> types)
    {
        // Ordered, so that which of two discoverers for one subject type is used does not depend on the load order.
        var discovererTypes = types
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition && typeof(ISubjectDiscoverer).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        var discoverers = new List<ISubjectDiscoverer>();
        var discoverersBySubjectType = new Dictionary<Type, Type>();
        foreach (var type in discovererTypes)
        {
            try
            {
                var discoverer = (ISubjectDiscoverer)ActivatorUtilities.CreateInstance(_serviceProvider, type);
                if (!AcceptsDiscoveryResults(discoverer.SubjectType))
                {
                    _logger.LogError(
                        "The subject discoverer {Discoverer} is not used: {SubjectType} has no Setup or Edit component that implements {Interface}.",
                        type, discoverer.SubjectType, nameof(ISubjectDiscoveryComponent));
                    continue;
                }

                // The Discover page tells results apart by subject type and key, which is unique within one discoverer.
                if (!discoverersBySubjectType.TryAdd(discoverer.SubjectType, type))
                {
                    _logger.LogError(
                        "The subject discoverer {Discoverer} is not used: {SubjectType} already has the discoverer {UsedDiscoverer}.",
                        type, discoverer.SubjectType, discoverersBySubjectType[discoverer.SubjectType]);
                    continue;
                }

                discoverers.Add(discoverer);
            }
            catch (Exception exception)
            {
                // A broken plugin type, such as a TypeLoadException, must not take the other discoverers down.
                _logger.LogError(exception, "The subject discoverer {Discoverer} could not be created.", type);
            }
        }

        return discoverers.ToArray();
    }

    private bool AcceptsDiscoveryResults(Type subjectType)
    {
        var registration = _componentRegistry.GetEditorComponent(subjectType, isCreating: true);

        return registration is { TakesDiscoveryResults: true };
    }
}
