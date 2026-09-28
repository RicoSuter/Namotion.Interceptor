using System.Collections;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Minimal read-only dictionary wrapper that implements <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// but NOT non-generic <see cref="IDictionary"/>. Used in tests to exercise KVP-reflection fallback paths.
/// </summary>
public sealed class ReadOnlyDictionaryWrapper<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _inner;

    public ReadOnlyDictionaryWrapper(Dictionary<TKey, TValue> inner) => _inner = inner;

    public TValue this[TKey key] => _inner[key];
    public IEnumerable<TKey> Keys => _inner.Keys;
    public IEnumerable<TValue> Values => _inner.Values;
    public int Count => _inner.Count;
    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
    public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
