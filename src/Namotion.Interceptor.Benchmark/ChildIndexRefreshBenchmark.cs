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

public enum RelationshipConsumerConfiguration
{
    LifecycleOnly,
    Registry,
    RegistryAndParents
}

/// <summary>
/// Structural writes that exercise ordered relationship reconciliation. The lifecycle-only configuration
/// performs the same writes without a relationship consumer, which isolates compact membership-state cost.
/// </summary>
[MemoryDiagnoser]
public class ChildIndexRefreshBenchmark
{
    private RefreshContainer _replacementContainer;
    private RefreshContainer _reorderContainer;
    private RefreshContainer _rekeyContainer;
    private RefreshContainer _duplicateContainer;
    private RefreshContainer _sameInstanceContainer;

    private RefreshItem[] _replacementA;
    private RefreshItem[] _replacementB;
    private RefreshItem[] _sameOrder;
    private RefreshItem[] _reordered;
    private RefreshItem[] _duplicatesA;
    private RefreshItem[] _duplicatesB;
    private RefreshItem[] _sameInstance;

    private IReadOnlyDictionary<string, RefreshItem> _sameKeys;
    private IReadOnlyDictionary<string, RefreshItem> _rekeyed;

    private bool _replacementFlip;
    private bool _reorderFlip;
    private bool _rekeyFlip;
    private bool _duplicateFlip;

    [Params(4, 1000)]
    public int Count;

    [Params(
        RelationshipConsumerConfiguration.LifecycleOnly,
        RelationshipConsumerConfiguration.Registry,
        RelationshipConsumerConfiguration.RegistryAndParents)]
    public RelationshipConsumerConfiguration Consumers;

    [GlobalSetup]
    public void Setup()
    {
        var items = CreateItems(Count);

        _sameOrder = [.. items];
        _reordered = [.. items];
        Array.Reverse(_reordered);

        _replacementA = CreateItems(Count);
        _replacementB = CreateItems(Count);

        var duplicateItems = CreateItems(Count / 2);
        _duplicatesA = new RefreshItem[Count];
        _duplicatesB = new RefreshItem[Count];
        for (var index = 0; index < Count; index++)
        {
            _duplicatesA[index] = duplicateItems[index / 2];
            _duplicatesB[index] = duplicateItems[(Count - 1 - index) / 2];
        }

        _sameInstance = [.. items];

        var keys = new string[Count];
        var sameKeys = new Dictionary<string, RefreshItem>(Count);
        var rekeyed = new Dictionary<string, RefreshItem>(Count);
        for (var index = 0; index < Count; index++)
        {
            keys[index] = "key" + index;
            sameKeys[keys[index]] = items[index];
        }

        for (var index = 0; index < Count; index++)
        {
            rekeyed[index == 0 ? "moved" : keys[index]] = items[index];
        }

        _sameKeys = sameKeys;
        _rekeyed = rekeyed;

        _replacementContainer = CreateContainer();
        _replacementContainer.Items = _replacementB;

        _reorderContainer = CreateContainer();
        _reorderContainer.Items = _reordered;

        _rekeyContainer = CreateContainer();
        _rekeyContainer.ItemsByKey = _rekeyed;

        _duplicateContainer = CreateContainer();
        _duplicateContainer.Items = _duplicatesB;

        _sameInstanceContainer = CreateContainer();
        _sameInstanceContainer.Items = _sameInstance;
    }

    /// <summary>Replaces every collection membership with a new subject.</summary>
    [Benchmark]
    public void ReplaceCollection()
    {
        _replacementFlip = !_replacementFlip;
        _replacementContainer.Items = _replacementFlip ? _replacementA : _replacementB;
    }

    /// <summary>Reverses all retained collection occurrences.</summary>
    [Benchmark]
    public void ReorderCollection()
    {
        _reorderFlip = !_reorderFlip;
        _reorderContainer.Items = _reorderFlip ? _sameOrder : _reordered;
    }

    /// <summary>Moves one retained dictionary occurrence to another key.</summary>
    [Benchmark]
    public void RekeyDictionaryEntry()
    {
        _rekeyFlip = !_rekeyFlip;
        _rekeyContainer.ItemsByKey = _rekeyFlip ? _sameKeys : _rekeyed;
    }

    /// <summary>Reverses repeated occurrences while each distinct subject remains a member.</summary>
    [Benchmark]
    public void ReorderDuplicateOccurrences()
    {
        _duplicateFlip = !_duplicateFlip;
        _duplicateContainer.Items = _duplicateFlip ? _duplicatesA : _duplicatesB;
    }

    /// <summary>Mutates and assigns the same collection instance to request an explicit structural refresh.</summary>
    [Benchmark]
    public void RefreshSameInstanceCollection()
    {
        (_sameInstance[0], _sameInstance[^1]) = (_sameInstance[^1], _sameInstance[0]);
        _sameInstanceContainer.Items = _sameInstance;
    }

    private RefreshContainer CreateContainer()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        switch (Consumers)
        {
            case RelationshipConsumerConfiguration.LifecycleOnly:
                break;

            case RelationshipConsumerConfiguration.Registry:
                context.WithRegistry();
                break;

            case RelationshipConsumerConfiguration.RegistryAndParents:
                context.WithParents();
                context.WithRegistry();
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }

        return new RefreshContainer(context);
    }

    private static RefreshItem[] CreateItems(int count)
    {
        var items = new RefreshItem[count];
        for (var index = 0; index < count; index++)
        {
            items[index] = new RefreshItem();
        }

        return items;
    }
}
