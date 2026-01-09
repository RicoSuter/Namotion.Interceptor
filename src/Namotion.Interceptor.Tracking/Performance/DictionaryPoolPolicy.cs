using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Tracking.Performance;

/// <summary>
/// Pool policy for <see cref="Dictionary{TKey, TValue}"/> that clears on return.
/// </summary>
/// <typeparam name="TKey">The type of keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of values in the dictionary.</typeparam>
public sealed class DictionaryPoolPolicy<TKey, TValue> : PooledObjectPolicy<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    private readonly int _initialCapacity;

    public DictionaryPoolPolicy(int initialCapacity = 4)
    {
        _initialCapacity = initialCapacity;
    }

    public override Dictionary<TKey, TValue> Create() => new(_initialCapacity);

    public override bool Return(Dictionary<TKey, TValue> obj)
    {
        obj.Clear();
        return true;
    }
}
