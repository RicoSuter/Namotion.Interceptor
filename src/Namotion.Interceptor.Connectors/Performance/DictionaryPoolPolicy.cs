using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Connectors.Performance;

/// <summary>
/// Pool policy for Dictionary&lt;TKey, TValue&gt;.
/// Does NOT clear on return for maximum performance - callers handle clearing.
/// </summary>
internal sealed class DictionaryPoolPolicy<TKey, TValue> : PooledObjectPolicy<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    public override Dictionary<TKey, TValue> Create() => new();

    public override bool Return(Dictionary<TKey, TValue> obj) => true;
}
