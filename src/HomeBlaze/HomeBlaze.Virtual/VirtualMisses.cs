using Namotion.Interceptor;

namespace HomeBlaze.Virtual;

/// <summary>
/// Hands the misses of the evaluations of one virtual property to the awaiter. The first evaluation runs while the
/// property is added, before it is known, so its misses wait until <see cref="Attach"/>.
/// </summary>
internal sealed class VirtualMisses(VirtualPropertyAwaiter awaiter)
{
    private readonly Lock _lock = new();
    private PropertyReference? _owner;
    private List<VirtualMiss>? _pending;

    public void Attach(PropertyReference owner)
    {
        List<VirtualMiss>? pending;
        lock (_lock)
        {
            _owner = owner;
            pending = _pending;
            _pending = null;
        }

        if (pending is not null)
        {
            Await(owner, pending);
        }
    }

    public void Record(List<VirtualMiss>? misses)
    {
        if (misses is null)
        {
            return;
        }

        PropertyReference owner;
        lock (_lock)
        {
            if (_owner is not { } attached)
            {
                _pending = misses;
                return;
            }

            owner = attached;
        }

        Await(owner, misses);
    }

    // Outside the lock: the awaiter takes its own lock and the registry's.
    private void Await(PropertyReference owner, List<VirtualMiss> misses)
    {
        foreach (var miss in misses)
        {
            awaiter.Await(miss.Subject, miss.Name, owner);
        }
    }
}
