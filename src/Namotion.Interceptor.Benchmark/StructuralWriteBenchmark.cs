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

    private Dictionary<string, Car> _entries;
    private Dictionary<string, Car> _entriesCopy;
    private Dictionary<string, Car> _entriesWithOneAdded;
    private Dictionary<string, Car> _entriesWithOneReKeyed;

    private Dictionary<int, Car> _numberedEntries;
    private Dictionary<int, Car> _numberedEntriesCopy;

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

        _entries = Enumerable.Range(0, EntryCount).ToDictionary(index => $"Car{index}", _ => new Car());
        _entriesCopy = new Dictionary<string, Car>(_entries);
        _entriesWithOneAdded = new Dictionary<string, Car>(_entries) { ["Added"] = new Car() };

        _entriesWithOneReKeyed = new Dictionary<string, Car>(_entries);
        _entriesWithOneReKeyed.Remove($"Car{EntryCount / 2}", out var reKeyed);
        _entriesWithOneReKeyed["Renamed"] = reKeyed!;

        _numberedEntries = Enumerable.Range(0, EntryCount).ToDictionary(index => index, _ => new Car());
        _numberedEntriesCopy = new Dictionary<int, Car>(_numberedEntries);

        _car = new Car(context) { PreviousCars = _items };
        _garage = new Garage(context) { ByName = _entries, ById = _numberedEntries };
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
    /// Every entry stays under its key.
    /// </summary>
    [Benchmark]
    public void ReplaceDictionaryKeepingEntries()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesCopy : _entries;
    }

    /// <summary>
    /// Every entry stays under its key, which is a value type and has to be boxed to be read.
    /// </summary>
    [Benchmark]
    public void ReplaceNumberedDictionaryKeepingEntries()
    {
        _toggle = !_toggle;
        _garage.ById = _toggle ? _numberedEntriesCopy : _numberedEntries;
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
}
