using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Structural writes that keep subjects in the property, which is what replacing a collection or
/// dictionary with an edited copy does to every entry the edit did not touch. Each write alternates
/// between two prepared values, so a measured operation is one reconciliation.
/// </summary>
[MemoryDiagnoser]
public class RegistryStructuralWriteBenchmark
{
    private const int EntryCount = 100;

    private Car _car;
    private Garage _garage;

    private Car[] _items;
    private Car[] _itemsCopy;

    private Dictionary<string, Car> _entries;
    private Dictionary<string, Car> _entriesCopy;
    private Dictionary<string, Car> _entriesWithOneAdded;
    private Dictionary<string, Car> _entriesWithOneReKeyed;
    private Dictionary<string, Car> _entriesReversed;
    private Dictionary<string, Car> _otherEntries;

    private Dictionary<int, Car> _numberedEntries;
    private Dictionary<int, Car> _numberedEntriesCopy;

    private bool _toggle;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _items = Enumerable.Range(0, EntryCount).Select(_ => new Car()).ToArray();
        _itemsCopy = [.. _items];

        _entries = Enumerable.Range(0, EntryCount).ToDictionary(index => $"Car{index}", _ => new Car());
        _entriesCopy = new Dictionary<string, Car>(_entries);

        _entriesWithOneAdded = new Dictionary<string, Car>(_entries) { ["Added"] = new Car() };

        _entriesWithOneReKeyed = new Dictionary<string, Car>(_entries);
        _entriesWithOneReKeyed.Remove($"Car{EntryCount / 2}", out var reKeyed);
        _entriesWithOneReKeyed["Renamed"] = reKeyed!;

        _entriesReversed = new Dictionary<string, Car>(_entries.Reverse());

        _otherEntries = Enumerable.Range(0, EntryCount).ToDictionary(index => $"Car{index}", _ => new Car());

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
    /// Every entry stays under its key.
    /// </summary>
    [Benchmark]
    public void ReplaceDictionaryKeepingEntries()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesCopy : _entries;
    }

    /// <summary>
    /// Every entry stays under its key, and the keys are value types, which the non-generic
    /// dictionary interfaces box.
    /// </summary>
    [Benchmark]
    public void ReplaceIntKeyedDictionaryKeepingEntries()
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

    /// <summary>
    /// Every entry stays under its key and the enumeration order turns around.
    /// </summary>
    [Benchmark]
    public void ReverseDictionaryEntries()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _entriesReversed : _entries;
    }

    /// <summary>
    /// No entry stays, so every subject is detached or attached.
    /// </summary>
    [Benchmark]
    public void ReplaceDictionaryEntries()
    {
        _toggle = !_toggle;
        _garage.ByName = _toggle ? _otherEntries : _entries;
    }
}
