using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Observes the structure of the graph for <see cref="AuthorizationService"/>: tells it when an <see cref="Authorization"/>
/// or a <see cref="Role"/> attaches or detaches, and counts the changes that
/// <see cref="AuthorizationService.GraphVersion"/> names. Thread-safe.
/// </summary>
// After the registry, so that a rebuild started by a change finds the subject among the known subjects, or no longer,
// and so that the version changes once the registry holds the change. The context has no way to remove a service, so
// Detach lets go of a disposed service while the observer stays.
[RunsAfter(typeof(SubjectRegistry))]
internal sealed class GraphObserver(Action rolesChanged) : ILifecycleHandler, IPropertyLifecycleHandler, IChildIndexHandler
{
    private Action? _rolesChanged = rolesChanged;
    private long _graphVersion;

    /// <summary>The number of changes counted.</summary>
    public long GraphVersion => Volatile.Read(ref _graphVersion);

    /// <summary>Stops telling the service about changes of the roles, so that the context no longer holds it.</summary>
    public void Detach() => Volatile.Write(ref _rolesChanged, null);

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        Increment();
        if (change.Subject is Authorization or Role)
        {
            Volatile.Read(ref _rolesChanged)?.Invoke();
        }
    }

    public void AttachProperty(SubjectPropertyLifecycleChange change) => Increment();

    public void DetachProperty(SubjectPropertyLifecycleChange change) => Increment();

    public void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children) => Increment();

    /// <summary>Counts a change of the metadata of a subject or of one of its properties, named as the storage adds them.</summary>
    public void OnPropertyChanged(SubjectPropertyChange change)
    {
        if (change.Property.Name.EndsWith(KnownAttributes.Metadata, StringComparison.Ordinal))
        {
            Increment();
        }
    }

    private void Increment() => Interlocked.Increment(ref _graphVersion);
}
