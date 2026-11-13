using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Virtual;

/// <summary>
/// A nested subject of a virtual subject, created from an entry with properties. Not configurable: it is edited and
/// saved with the virtual subject it belongs to.
/// </summary>
[InterceptorSubject]
public partial class VirtualNode : ILifecycleHandler
{
    private readonly IReadOnlyDictionary<string, VirtualProperty> _entries;
    private readonly string _path;
    private readonly ImmutableHashSet<string> _rejected;
    private readonly VirtualLevel _level;

    internal VirtualNode(IReadOnlyDictionary<string, VirtualProperty> entries, string path, ImmutableHashSet<string> rejected, VirtualRuntime runtime)
    {
        _entries = entries;
        _path = path;
        _rejected = rejected;
        _level = new VirtualLevel(this, runtime, VirtualEntryPlace.Nested);
    }

    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!ReferenceEquals(change.Subject, this))
        {
            return;
        }

        if (change.IsContextAttach)
        {
            _level.Attach(_entries, _path, _rejected);
        }
        else if (change.IsContextDetach)
        {
            _level.Detach();
        }
    }
}
