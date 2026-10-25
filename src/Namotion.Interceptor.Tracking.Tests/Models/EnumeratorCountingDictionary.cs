using System.Collections;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A non-generic dictionary that hands out disposable enumerators and counts how many were created
/// and how many were disposed, so a scan that has to release its enumerator can be checked.
/// </summary>
public sealed class EnumeratorCountingDictionary(IDictionary inner) : IDictionary
{
    public int CreatedEnumerators { get; private set; }

    public int DisposedEnumerators { get; private set; }

    public object? this[object key]
    {
        get => inner[key];
        set => inner[key] = value;
    }

    public ICollection Keys => inner.Keys;

    public ICollection Values => inner.Values;

    public bool IsFixedSize => inner.IsFixedSize;

    public bool IsReadOnly => inner.IsReadOnly;

    public int Count => inner.Count;

    public bool IsSynchronized => inner.IsSynchronized;

    public object SyncRoot => inner.SyncRoot;

    public void Add(object key, object? value) => inner.Add(key, value);

    public void Clear() => inner.Clear();

    public bool Contains(object key) => inner.Contains(key);

    public void Remove(object key) => inner.Remove(key);

    public void CopyTo(Array array, int index) => inner.CopyTo(array, index);

    public IDictionaryEnumerator GetEnumerator()
    {
        CreatedEnumerators++;
        return new Enumerator(this, inner.GetEnumerator());
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Enumerator(EnumeratorCountingDictionary owner, IDictionaryEnumerator inner)
        : IDictionaryEnumerator, IDisposable
    {
        public DictionaryEntry Entry => inner.Entry;

        public object Key => inner.Key;

        public object? Value => inner.Value;

        public object Current => inner.Current;

        public bool MoveNext() => inner.MoveNext();

        public void Reset() => inner.Reset();

        public void Dispose() => owner.DisposedEnumerators++;
    }
}
