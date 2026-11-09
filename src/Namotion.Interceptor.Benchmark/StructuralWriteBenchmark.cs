using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

public enum StructuralWriteHandler
{
    /// <summary>The registry, which keeps the index of each child and of each parent entry.</summary>
    Registry,

    /// <summary>Parent tracking without a registry, so that it is as large a share of a write as it can get.</summary>
    ParentTracking
}

/// <summary>
/// Structural writes that keep or move subjects in the property, measured with each handler that
/// keeps the index of a child. Each write alternates between two prepared values, so a measured
/// operation is one reconciliation.
/// </summary>
[MemoryDiagnoser]
public class StructuralWriteBenchmark
{
    private const int EntryCount = 100;

    private Car _car;
    private Garage _garage;

    private Car[] _items;
    private Car[] _itemsCopy;
    private Car[] _itemsRotated;
    private Car[] _otherItems;
    private Car[] _itemsWithFirstRepeated;
    private Car[] _itemsWithFirstRepeatedCopy;
    private Car[] _itemsWithSecondRepeated;

    private Dictionary<string, Car> _entries;
    private Dictionary<string, Car> _entriesCopy;
    private Dictionary<string, Car> _entriesWithOneAdded;
    private Dictionary<string, Car> _entriesWithOneReKeyed;
    private Dictionary<string, Car> _entriesWithAlias;
    private Dictionary<string, Car> _entriesWithAliasCopy;

    private bool _toggle;

    [Params(StructuralWriteHandler.Registry, StructuralWriteHandler.ParentTracking)]
    public StructuralWriteHandler Handler;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context = Handler == StructuralWriteHandler.Registry
            ? context.WithRegistry()
            : context.WithParents();

        _items = Enumerable.Range(0, EntryCount).Select(_ => new Car()).ToArray();
        _itemsCopy = [.. _items];
        _itemsRotated = [.. _items.Skip(1), _items[0]];
        _otherItems = Enumerable.Range(0, EntryCount).Select(_ => new Car()).ToArray();
        _itemsWithFirstRepeated = [.. _items, _items[0]];
        _itemsWithFirstRepeatedCopy = [.. _itemsWithFirstRepeated];
        _itemsWithSecondRepeated = [.. _items, _items[1]];

        _entries = Enumerable.Range(0, EntryCount).ToDictionary(index => $"Car{index}", _ => new Car());
        _entriesCopy = new Dictionary<string, Car>(_entries);
        _entriesWithOneAdded = new Dictionary<string, Car>(_entries) { ["Added"] = new Car() };

        _entriesWithOneReKeyed = new Dictionary<string, Car>(_entries);
        _entriesWithOneReKeyed.Remove($"Car{EntryCount / 2}", out var reKeyed);
        _entriesWithOneReKeyed["Renamed"] = reKeyed!;

        _entriesWithAlias = new Dictionary<string, Car>(_entries) { ["Alias"] = _entries["Car0"] };
        _entriesWithAliasCopy = new Dictionary<string, Car>(_entriesWithAlias);

        _car = new Car(context) { PreviousCars = _items };
        _garage = new Garage(context) { ByName = _entries };
    }

    /// <summary>
    /// Every item stays at its position.
    /// </summary>
    [Benchmark]
    public void ReplaceCollectionKeepingItems()
    {
        _toggle = !_toggle;
        _car.PreviousCars = _toggle ? _itemsCopy : _items;
    }

    /// <summary>
    /// Every item stays and moves to another position.
    /// </summary>
    [Benchmark]
    public void MoveCollectionItems()
    {
        _toggle = !_toggle;
        _car.PreviousCars = _toggle ? _itemsRotated : _items;
    }

    /// <summary>
    /// No item stays, so every subject is detached or attached.
    /// </summary>
    [Benchmark]
    public void ReplaceCollectionItems()
    {
        _toggle = !_toggle;
        _car.PreviousCars = _toggle ? _otherItems : _items;
    }

    /// <summary>
    /// Every item stays at its positions, one of them at two.
    /// </summary>
    [Benchmark]
    public void ReplaceCollectionHoldingAnItemTwice()
    {
        _toggle = !_toggle;
        _car.PreviousCars = _toggle ? _itemsWithFirstRepeatedCopy : _itemsWithFirstRepeated;
    }

    /// <summary>
    /// Every item stays at its first position, and the last position goes from one item held twice to another.
    /// </summary>
    [Benchmark]
    public void ChangeRepeatedCollectionItem()
    {
        _toggle = !_toggle;
        _car.PreviousCars = _toggle ? _itemsWithSecondRepeated : _itemsWithFirstRepeated;
    }

    /// <summary>
    /// Every entry stays under its key.
    /// </summary>
    [Benchmark]
    public void ReplaceDictionaryKeepingEntries()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesCopy : _entries;
    }

    /// <summary>
    /// One entry is added or removed and the rest stay, which is what editing a copy does.
    /// </summary>
    [Benchmark]
    public void AddOrRemoveDictionaryEntry()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesWithOneAdded : _entries;
    }

    /// <summary>
    /// One entry moves to another key and the rest stay.
    /// </summary>
    [Benchmark]
    public void ReKeyDictionaryEntry()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesWithOneReKeyed : _entries;
    }

    /// <summary>
    /// Every entry stays under its key, one subject under two.
    /// </summary>
    [Benchmark]
    public void ReplaceDictionaryHoldingASubjectTwice()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesWithAliasCopy : _entriesWithAlias;
    }

    /// <summary>
    /// A subject gains or loses a second key and the rest stay.
    /// </summary>
    [Benchmark]
    public void AddOrRemoveSecondKeyOfASubject()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesWithAlias : _entries;
    }
}
