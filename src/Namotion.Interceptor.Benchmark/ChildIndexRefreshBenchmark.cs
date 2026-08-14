using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[InterceptorSubject]
public partial class RefreshItem
{
    public partial int Value { get; set; }
}

[InterceptorSubject]
public partial class RefreshContainer
{
    public RefreshContainer()
    {
        Items = [];
        ItemsByKey = new Dictionary<string, RefreshItem>();
    }

    public partial RefreshItem[] Items { get; set; }

    public partial IReadOnlyDictionary<string, RefreshItem> ItemsByKey { get; set; }
}

/// <summary>
/// Container writes which retain their children, the only writes that reach the child-index refresh.
/// Each case alternates between two prebuilt values, so the reference-equality shortcut never skips a
/// write and the reordering cases reorder on every call. <see cref="ReplaceCollection"/> retains
/// nothing and is the reference row for writes the refresh cannot reach.
/// </summary>
[MemoryDiagnoser]
public class ChildIndexRefreshBenchmark
{
    private RefreshContainer _container;

    private RefreshItem[] _sameOrderA;
    private RefreshItem[] _sameOrderB;
    private RefreshItem[] _reversed;
    private RefreshItem[] _shifted;
    private RefreshItem[] _replacementA;
    private RefreshItem[] _replacementB;

    private IReadOnlyDictionary<string, RefreshItem> _sameKeysA;
    private IReadOnlyDictionary<string, RefreshItem> _sameKeysB;
    private IReadOnlyDictionary<string, RefreshItem> _rekeyed;
    private IReadOnlyDictionary<string, RefreshItem> _reversedKeys;

    private bool _flip;

    [Params(4, 1000)]
    public int Count;

    /// <summary>
    /// Parent tracking keeps a second copy of each child index, refreshed per retained child.
    /// </summary>
    [Params(false, true)]
    public bool TrackParents;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        if (TrackParents)
        {
            context.WithParents();
        }

        context.WithRegistry();

        var items = new RefreshItem[Count];
        for (var i = 0; i < Count; i++)
        {
            items[i] = new RefreshItem();
        }

        _sameOrderA = [.. items];
        _sameOrderB = [.. items];

        _reversed = [.. items];
        Array.Reverse(_reversed);

        _shifted = new RefreshItem[Count];
        for (var i = 0; i < Count; i++)
        {
            _shifted[i] = items[(i + Count - 1) % Count];
        }

        _replacementA = new RefreshItem[Count];
        _replacementB = new RefreshItem[Count];
        for (var i = 0; i < Count; i++)
        {
            _replacementA[i] = new RefreshItem();
            _replacementB[i] = new RefreshItem();
        }

        var sameKeysA = new Dictionary<string, RefreshItem>(Count);
        var sameKeysB = new Dictionary<string, RefreshItem>(Count);
        var rekeyed = new Dictionary<string, RefreshItem>(Count);
        var reversedKeys = new Dictionary<string, RefreshItem>(Count);
        for (var i = 0; i < Count; i++)
        {
            sameKeysA["k" + i] = items[i];
            sameKeysB["k" + i] = items[i];
            rekeyed[i == 0 ? "moved" : "k" + i] = items[i];
            reversedKeys["k" + (Count - 1 - i)] = items[Count - 1 - i];
        }

        _sameKeysA = sameKeysA;
        _sameKeysB = sameKeysB;
        _rekeyed = rekeyed;
        _reversedKeys = reversedKeys;

        // Deliberately the B value: the first write of each case then differs by reference from what the
        // property already holds, so no invocation is skipped by the reference-equality shortcut.
        _container = new RefreshContainer(context) { Items = _sameOrderB };
    }

    /// <summary>Same children in the same order: the refresh finds nothing to move.</summary>
    [Benchmark]
    public void RewriteCollectionSameOrder()
    {
        _flip = !_flip;
        _container.Items = _flip ? _sameOrderA : _sameOrderB;
    }

    /// <summary>Every child moves by one position, the shape a prepend or a remove-from-front produces.</summary>
    [Benchmark]
    public void ShiftCollectionByOne()
    {
        _flip = !_flip;
        _container.Items = _flip ? _sameOrderA : _shifted;
    }

    /// <summary>Worst case for placement: every child moves, and the incoming order is the reverse of the stored one.</summary>
    [Benchmark]
    public void ReverseCollection()
    {
        _flip = !_flip;
        _container.Items = _flip ? _sameOrderA : _reversed;
    }

    /// <summary>Retains nothing, so the refresh is never dispatched. Reference row for unreached writes.</summary>
    [Benchmark]
    public void ReplaceCollection()
    {
        _flip = !_flip;
        _container.Items = _flip ? _replacementA : _replacementB;
    }

    /// <summary>Same children under the same keys: the refresh finds nothing to move.</summary>
    [Benchmark]
    public void RewriteDictionarySameKeys()
    {
        _flip = !_flip;
        _container.ItemsByKey = _flip ? _sameKeysA : _sameKeysB;
    }

    /// <summary>One child moves to another key, which is the case master never refreshed.</summary>
    [Benchmark]
    public void RekeyOneDictionaryEntry()
    {
        _flip = !_flip;
        _container.ItemsByKey = _flip ? _sameKeysA : _rekeyed;
    }

    /// <summary>
    /// Same keys and same children, enumerated in the opposite order: no index changes at all, yet every child
    /// has to move. The children of a dictionary were left in place before this refresh became shape-agnostic,
    /// so this is the row where that regression, if any, shows up.
    /// </summary>
    [Benchmark]
    public void ReorderDictionary()
    {
        _flip = !_flip;
        _container.ItemsByKey = _flip ? _sameKeysA : _reversedKeys;
    }
}
