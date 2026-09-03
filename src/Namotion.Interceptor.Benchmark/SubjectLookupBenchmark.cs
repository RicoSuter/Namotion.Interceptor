using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[InterceptorSubject]
public partial class TireStore
{
    public TireStore()
    {
        TiresByName = new Dictionary<string, Tire>();
    }

    public partial IReadOnlyDictionary<string, Tire> TiresByName { get; set; }
}

/// <summary>
/// Exposes only <see cref="IReadOnlyDictionary{TKey,TValue}"/>, so neither the non-generic
/// <see cref="IDictionary"/> indexer nor <see cref="IDictionary{TKey,TValue}"/> is available.
/// </summary>
public sealed class ReadOnlyOnlyDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _inner;

    public ReadOnlyOnlyDictionary(Dictionary<TKey, TValue> inner) => _inner = inner;

    public TValue this[TKey key] => _inner[key];
    public IEnumerable<TKey> Keys => _inner.Keys;
    public IEnumerable<TValue> Values => _inner.Values;
    public int Count => _inner.Count;
    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
    public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
}

/// <summary>
/// Exposes only <see cref="IDictionary{TKey,TValue}"/>, so neither the non-generic
/// <see cref="IDictionary"/> indexer nor <see cref="IReadOnlyDictionary{TKey,TValue}"/> is
/// available. This is the shape whose lookup complexity the fix changes.
/// </summary>
public sealed class GenericOnlyDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _inner;

    public GenericOnlyDictionary(Dictionary<TKey, TValue> inner) => _inner = inner;

    public TValue this[TKey key] { get => _inner[key]; set => _inner[key] = value; }
    public ICollection<TKey> Keys => _inner.Keys;
    public ICollection<TValue> Values => _inner.Values;
    public int Count => _inner.Count;
    public bool IsReadOnly => false;
    public void Add(TKey key, TValue value) => _inner.Add(key, value);
    public void Add(KeyValuePair<TKey, TValue> item) => _inner.Add(item.Key, item.Value);
    public void Clear() => _inner.Clear();
    public bool Contains(KeyValuePair<TKey, TValue> item) => _inner.ContainsKey(item.Key);
    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => throw new NotSupportedException();
    public bool Remove(TKey key) => _inner.Remove(key);
    public bool Remove(KeyValuePair<TKey, TValue> item) => _inner.Remove(item.Key);
    public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
}

/// <summary>
/// Isolates <see cref="SubjectLookup.FindSubjectInDictionary"/>, the only keyed subject lookup in
/// the framework, and the path resolution that calls it. Rows cover a hit and a miss so the cost of
/// the key type guard is visible separately from the cost of the successful read, and one row per
/// dictionary shape the lookup dispatches on, because the shapes move in opposite directions:
/// the <see cref="Hashtable"/> row is the one made strictly slower (a cache lookup that always
/// returns null before the same indexer call as before), and the
/// <see cref="GenericOnlyDictionary{TKey,TValue}"/> row is the one made faster (a key value pair
/// scan becomes a single typed lookup).
/// <see cref="FindSubjectInList"/> is the local control: it shares this class's setup but the
/// dictionary lookup change cannot reach it.
/// </summary>
/// <remarks>
/// One cost is deliberately not measured here: the first lookup against a given dictionary runtime
/// type compiles an expression tree. That is once per type for the life of the process, it never
/// recurs, and <see cref="Setup"/> performs one lookup of every shape so no row prices it.
/// </remarks>
[MemoryDiagnoser]
public class SubjectLookupBenchmark
{
    private Dictionary<string, Tire> _dictionary;
    private Dictionary<int, Tire> _intKeyedDictionary;
    private ImmutableDictionary<string, Tire> _immutableDictionary;
    private Hashtable _hashtable;
    private ReadOnlyOnlyDictionary<string, Tire> _readOnlyOnlyDictionary;
    private GenericOnlyDictionary<string, Tire> _genericOnlyDictionary;
    private List<Tire> _list;

    // Boxed once so the int keyed row measures the lookup rather than a boxing allocation.
    private object _intKey;

    private TireStore _store;
    private RegisteredSubject _storeRegistered;
    private PathProviderBase _pathProvider;

    [GlobalSetup]
    public void Setup()
    {
        _dictionary = new Dictionary<string, Tire>();
        _intKeyedDictionary = new Dictionary<int, Tire>();
        _hashtable = new Hashtable();
        _list = [];

        for (var index = 0; index < 16; index++)
        {
            var tire = new Tire();
            _dictionary["tire" + index] = tire;
            _intKeyedDictionary[index] = tire;
            _hashtable["tire" + index] = tire;
            _list.Add(tire);
        }

        _immutableDictionary = _dictionary.ToImmutableDictionary();
        _readOnlyOnlyDictionary = new ReadOnlyOnlyDictionary<string, Tire>(_dictionary);
        _genericOnlyDictionary = new GenericOnlyDictionary<string, Tire>(_dictionary);
        _intKey = 8;

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _store = new TireStore(context);

        var tires = new Dictionary<string, Tire>();
        for (var index = 0; index < 16; index++)
        {
            tires["tire" + index] = new Tire();
        }

        _store.TiresByName = tires;
        _storeRegistered = _store.TryGetRegisteredSubject()!;
        _pathProvider = DefaultPathProvider.Instance;

        // Compile the per type expression tree here so no row prices that one-time cost.
        FindSubjectInDictionaryHit();
        FindSubjectInIntKeyedDictionaryHit();
        FindSubjectInImmutableDictionaryHit();
        FindSubjectInHashtableHit();
        FindSubjectInReadOnlyOnlyDictionaryHit();
        FindSubjectInGenericOnlyDictionaryHit();
        TryGetSubjectFromDictionaryPath();
    }

    [Benchmark]
    public object? FindSubjectInDictionaryHit()
    {
        return SubjectLookup.FindSubjectInDictionary(_dictionary, "tire8");
    }

    [Benchmark]
    public object? FindSubjectInDictionaryMiss()
    {
        return SubjectLookup.FindSubjectInDictionary(_dictionary, "absent");
    }

    [Benchmark]
    public object? FindSubjectInIntKeyedDictionaryHit()
    {
        return SubjectLookup.FindSubjectInDictionary(_intKeyedDictionary, _intKey);
    }

    [Benchmark]
    public object? FindSubjectInImmutableDictionaryHit()
    {
        return SubjectLookup.FindSubjectInDictionary(_immutableDictionary, "tire8");
    }

    [Benchmark]
    public object? FindSubjectInHashtableHit()
    {
        return SubjectLookup.FindSubjectInDictionary(_hashtable, "tire8");
    }

    [Benchmark]
    public object? FindSubjectInReadOnlyOnlyDictionaryHit()
    {
        return SubjectLookup.FindSubjectInDictionary(_readOnlyOnlyDictionary, "tire8");
    }

    [Benchmark]
    public object? FindSubjectInGenericOnlyDictionaryHit()
    {
        return SubjectLookup.FindSubjectInDictionary(_genericOnlyDictionary, "tire8");
    }

    [Benchmark]
    public object? FindSubjectInList()
    {
        return SubjectLookup.FindSubjectInCollection(_list, 8);
    }

    [Benchmark]
    public object? TryGetSubjectFromDictionaryPath()
    {
        var subject = _pathProvider.TryGetSubjectFromPath(_storeRegistered, "TiresByName[tire8]");
        if (subject is null)
        {
            throw new InvalidOperationException();
        }

        return subject;
    }
}
