using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Creates the subject discoverers among the loaded types that the create dialog can use.
/// </summary>
public sealed class SubjectDiscovererRegistry
{
    private readonly TypeProvider _typeProvider;
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubjectDiscovererRegistry> _logger;
    private readonly Lock _lock = new();

    private IReadOnlyCollection<Type>? _scannedTypes;
    private ISubjectDiscoverer[] _discoverers = [];

    public SubjectDiscovererRegistry(
        TypeProvider typeProvider,
        SubjectComponentRegistry componentRegistry,
        IServiceProvider serviceProvider,
        ILogger<SubjectDiscovererRegistry> logger)
    {
        _typeProvider = typeProvider;
        _componentRegistry = componentRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets one instance of every non-abstract <see cref="ISubjectDiscoverer"/> type whose subject type has a Setup or
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>. A discoverer that cannot be created,
    /// or has no such component, is left out and logged as an error.
    /// </summary>
    public IReadOnlyList<ISubjectDiscoverer> GetDiscoverers()
    {
        lock (_lock)
        {
            // Read inside the lock: a thread holding an older snapshot must not roll the list back over a newer one.
            var types = _typeProvider.Types;
            // The type provider publishes a new collection whenever types are added, such as by a plugin.
            if (!ReferenceEquals(types, _scannedTypes))
            {
                _discoverers = CreateDiscoverers(types);
                _scannedTypes = types;
            }

            return _discoverers;
        }
    }

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
                        "The subject discoverer {Discoverer} is not used: the component the create dialog shows for {SubjectType} does not implement {Interface}.",
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
        // The create dialog shows the Setup component, or the Edit component when there is none.
        var registration =
            _componentRegistry.GetComponent(subjectType, SubjectComponentType.Setup) ??
            _componentRegistry.GetComponent(subjectType, SubjectComponentType.Edit);

        return registration is { TakesDiscoveryResults: true };
    }
}
