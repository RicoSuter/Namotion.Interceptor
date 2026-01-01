using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

/// <summary>
/// Pool policy for List&lt;T&gt; with configurable initial capacity.
/// Does NOT clear on return for maximum performance - callers handle clearing.
/// </summary>
internal sealed class ListPoolPolicy<T> : PooledObjectPolicy<List<T>>
{
    private readonly int _initialCapacity;

    public ListPoolPolicy(int initialCapacity = 0)
    {
        _initialCapacity = initialCapacity;
    }

    public override List<T> Create() => new(_initialCapacity);

    public override bool Return(List<T> obj) => true;
}
