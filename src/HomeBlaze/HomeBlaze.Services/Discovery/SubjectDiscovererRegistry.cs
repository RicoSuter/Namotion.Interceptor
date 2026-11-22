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
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>. A discoverer that cannot be created,
    /// or has no such component, is left out and logged as an error.
    /// </summary>
    /// <remarks>
    /// The discoverers are created once, on the first call, from the types loaded then; types added later are not
    /// scanned, so every discoverer type must be loaded before.
    /// </remarks>
    public IReadOnlyList<ISubjectDiscoverer> GetDiscoverers() => _discoverers.Value;

    private ISubjectDiscoverer[] CreateDiscoverers(IReadOnlyCollection<Type> types)
    {
        var discoverers = new List<ISubjectDiscoverer>();
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(ISubjectDiscoverer).IsAssignableFrom(type))
            {
                continue;
            }

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
