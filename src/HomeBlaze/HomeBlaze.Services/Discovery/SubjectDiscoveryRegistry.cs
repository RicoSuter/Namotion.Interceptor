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
    private readonly TypeProvider _typeProvider;
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubjectDiscoveryRegistry> _logger;
    private readonly Lock _rebuildLock = new();

    // Guarded by _rebuildLock. Kept across rebuilds, because a running scan may still use an instance. Null marks a
    // type that could not be created or whose subject type could not be read, which is not tried again.
    private readonly Dictionary<Type, CreatedDiscovery?> _createdDiscoveries = [];

    // Guarded by _rebuildLock, so that a rebuild does not log a rejection again.
    private readonly HashSet<(Type DiscoveryType, Rejection Rejection)> _loggedRejections = [];

    // Guarded by _rebuildLock, which is re-entrant: a constructor that asks for the subject discoveries would
    // otherwise recurse until the stack overflows.
    private bool _isRebuilding;

    private Snapshot? _snapshot;

    public SubjectDiscoveryRegistry(
        TypeProvider typeProvider,
        SubjectComponentRegistry componentRegistry,
        IServiceProvider serviceProvider,
        ILogger<SubjectDiscoveryRegistry> logger)
    {
        _typeProvider = typeProvider;
        _componentRegistry = componentRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private enum Rejection
    {
        NoDiscoveryComponent,
        ComponentLookupFailed,
        DuplicateSubjectType
    }

    /// <summary>
    /// Gets one instance of every non-abstract <see cref="ISubjectDiscovery"/> type whose subject type has a Setup or
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>, at most one per subject type. A
    /// subject discovery is left out and logged as an error, once per type and reason, when it cannot be created, its
    /// subject type cannot be read, the lookup of its component fails, it has no such component, or a subject
    /// discovery earlier in the ordinal order of the full type names already finds its subject type.
    /// </summary>
    /// <remarks>
    /// Types added to <see cref="TypeProvider"/> later are picked up on the next call, and each subject discovery type
    /// is created only once. The result depends only on the loaded types, so a subject discovery type added later that
    /// sorts before the one in use for its subject type replaces it from the next call on.
    /// </remarks>
    public IReadOnlyList<ISubjectDiscovery> GetSubjectDiscoveries()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot is not null && ReferenceEquals(snapshot.Types, _typeProvider.Types)
            ? snapshot.SubjectDiscoveries
            : Rebuild();
    }

    private ISubjectDiscovery[] Rebuild()
    {
        lock (_rebuildLock)
        {
            var types = _typeProvider.Types;
            var snapshot = _snapshot;
            if (snapshot is not null && ReferenceEquals(snapshot.Types, types))
            {
                return snapshot.SubjectDiscoveries;
            }

            if (_isRebuilding)
            {
                throw new InvalidOperationException("GetSubjectDiscoveries was called while the subject discoveries are created.");
            }

            _isRebuilding = true;
            try
            {
                snapshot = new Snapshot(types, CreateSubjectDiscoveries(types));
            }
            finally
            {
                _isRebuilding = false;
            }

            Volatile.Write(ref _snapshot, snapshot);
            return snapshot.SubjectDiscoveries;
        }
    }

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
            if (GetOrCreateDiscovery(type) is not { } created)
            {
                continue;
            }

            var subjectType = created.SubjectType;
            bool acceptsResults;
            try
            {
                acceptsResults = AcceptsDiscoveryResults(subjectType);
            }
            catch (Exception exception)
            {
                // A subject type from a broken plugin, such as one whose interfaces throw a TypeLoadException.
                if (_loggedRejections.Add((type, Rejection.ComponentLookupFailed)))
                {
                    _logger.LogError(
                        exception, "The subject discovery {Discovery} is not used: the component of {SubjectType} could not be looked up.",
                        type, subjectType);
                }

                continue;
            }

            if (!acceptsResults)
            {
                if (_loggedRejections.Add((type, Rejection.NoDiscoveryComponent)))
                {
                    _logger.LogError(
                        "The subject discovery {Discovery} is not used: {SubjectType} has no Setup or Edit component that implements {Interface}.",
                        type, subjectType, nameof(ISubjectDiscoveryComponent));
                }

                continue;
            }

            // The Discover page tells results apart by subject type and key, which is unique within one subject discovery.
            if (!discoveriesBySubjectType.TryAdd(subjectType, type))
            {
                if (_loggedRejections.Add((type, Rejection.DuplicateSubjectType)))
                {
                    _logger.LogError(
                        "The subject discovery {Discovery} is not used: {SubjectType} already has the subject discovery {UsedDiscovery}.",
                        type, subjectType, discoveriesBySubjectType[subjectType]);
                }

                continue;
            }

            discoveries.Add(created.Discovery);
        }

        return discoveries.ToArray();
    }

    private CreatedDiscovery? GetOrCreateDiscovery(Type type)
    {
        if (_createdDiscoveries.TryGetValue(type, out var created))
        {
            return created;
        }

        try
        {
            var discovery = (ISubjectDiscovery)ActivatorUtilities.CreateInstance(_serviceProvider, type);
            var subjectType = discovery.SubjectType ?? throw new InvalidOperationException($"{type} has no subject type.");
            created = new CreatedDiscovery(discovery, subjectType);
        }
        catch (Exception exception)
        {
            // A broken plugin type, such as a TypeLoadException, must not take the other subject discoveries down.
            _logger.LogError(exception, "The subject discovery {Discovery} could not be created.", type);
            created = null;
        }

        _createdDiscoveries[type] = created;
        return created;
    }

    private bool AcceptsDiscoveryResults(Type subjectType)
    {
        // Asks the component registry on each rebuild, so a component that arrived after an earlier rebuild counts.
        var registration = _componentRegistry.GetEditorComponent(subjectType, isCreating: true);

        return registration is { TakesDiscoveryResults: true };
    }

    private sealed record Snapshot(IReadOnlyCollection<Type> Types, ISubjectDiscovery[] SubjectDiscoveries);

    private sealed record CreatedDiscovery(ISubjectDiscovery Discovery, Type SubjectType);
}
