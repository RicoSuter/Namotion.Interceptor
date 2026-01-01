using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

/// <summary>
/// Pool policy for HashSet&lt;T&gt;.
/// Does NOT clear on return for maximum performance - callers handle clearing.
/// </summary>
internal sealed class HashSetPoolPolicy<T> : PooledObjectPolicy<HashSet<T>>
{
    public override HashSet<T> Create() => [];

    public override bool Return(HashSet<T> obj) => true;
}
