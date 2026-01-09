using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

/// <summary>
/// Pool policy for <see cref="List{T}"/> that clears on return.
/// </summary>
/// <typeparam name="T">The type of elements in the list.</typeparam>
public sealed class ListPoolPolicy<T> : PooledObjectPolicy<List<T>>
{
    private readonly int _initialCapacity;

    public ListPoolPolicy(int initialCapacity = 4)
    {
        _initialCapacity = initialCapacity;
    }

    public override List<T> Create() => new(_initialCapacity);

    public override bool Return(List<T> obj)
    {
        obj.Clear();
        return true;
    }
}
