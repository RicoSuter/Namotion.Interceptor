using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

/// <summary>
/// Pool policy for <see cref="HashSet{T}"/> that clears on return.
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public sealed class HashSetPoolPolicy<T> : PooledObjectPolicy<HashSet<T>>
{
    private readonly int _initialCapacity;

    public HashSetPoolPolicy(int initialCapacity = 4)
    {
        _initialCapacity = initialCapacity;
    }

    public override HashSet<T> Create() => new(_initialCapacity);

    public override bool Return(HashSet<T> obj)
    {
        obj.Clear();
        return true;
    }
}
