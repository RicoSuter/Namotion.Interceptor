using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// Measures the two <see cref="IInterceptorSubject.Properties"/> dispatch shapes the base class
/// interception design had to choose between. The classes below are hand written mimics of the
/// generated code, not generated subjects, because the alternative shape was never emitted by the
/// generator: it was rejected on reasoning, and this benchmark exists to put a number behind that.
///
/// Chosen shape: every class in the hierarchy re-implements the explicit interface property and
/// resolves the instance overrides through a non-virtual protected helper in the root.
/// Rejected shape: only the root implements the explicit interface property, and it reaches the
/// per-type defaults through a protected virtual hook that every derived class overrides.
///
/// Both a monomorphic and a polymorphic call site are measured. The real hot path
/// (<see cref="PropertyReference.Metadata"/>) sees many subject types, so the polymorphic row is the
/// representative one; the monomorphic row shows what the JIT can recover when it sees a single type.
/// </summary>
[MemoryDiagnoser]
public class PropertiesDispatchShapeBenchmark
{
    private IInterceptorSubject _chosenLeaf = null!;
    private IInterceptorSubject _alternativeLeaf = null!;

    private IInterceptorSubject[] _chosenPolymorphic = null!;
    private IInterceptorSubject[] _alternativePolymorphic = null!;
    private IInterceptorSubject[] _generatedPolymorphic = null!;

    private PropertyReference _chosenReference;
    private PropertyReference _alternativeReference;

    [GlobalSetup]
    public void Setup()
    {
        _chosenLeaf = new ChosenLeaf();
        _alternativeLeaf = new AlternativeLeaf();

        _chosenPolymorphic = [new ChosenRoot(), new ChosenMiddle(), new ChosenLeaf()];
        _alternativePolymorphic = [new AlternativeRoot(), new AlternativeMiddle(), new AlternativeLeaf()];
        _generatedPolymorphic = [new BenchmarkRoot(), new BenchmarkMiddle(), new BenchmarkLeaf()];

        _chosenReference = new PropertyReference(_chosenLeaf, "RootValue");
        _alternativeReference = new PropertyReference(_alternativeLeaf, "RootValue");
    }

    [Benchmark] public int ChosenPropertiesCount() => _chosenLeaf.Properties.Count;
    [Benchmark] public int AlternativePropertiesCount() => _alternativeLeaf.Properties.Count;

    [Benchmark] public string ChosenMetadataLookup() => _chosenReference.Metadata.Name;
    [Benchmark] public string AlternativeMetadataLookup() => _alternativeReference.Metadata.Name;

    [Benchmark]
    public int ChosenPropertiesCountPolymorphic()
    {
        var total = 0;
        foreach (var subject in _chosenPolymorphic)
        {
            total += subject.Properties.Count;
        }

        return total;
    }

    [Benchmark]
    public int AlternativePropertiesCountPolymorphic()
    {
        var total = 0;
        foreach (var subject in _alternativePolymorphic)
        {
            total += subject.Properties.Count;
        }

        return total;
    }

    /// <summary>
    /// The same access over real generated subjects. <c>SubjectHierarchyBenchmark.PropertiesAccess</c>
    /// reads a single leaf, which the JIT folds to nothing, so it cannot show whether the emitted
    /// <c>Properties</c> member changed cost. This one keeps the call site polymorphic, which is what
    /// the shared reader in <see cref="PropertyReference.Metadata"/> sees.
    /// </summary>
    [Benchmark]
    public int GeneratedPropertiesCountPolymorphic()
    {
        var total = 0;
        foreach (var subject in _generatedPolymorphic)
        {
            total += subject.Properties.Count;
        }

        return total;
    }
}

internal static class MimicMetadata
{
    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> Build(Type owner, params string[] names)
    {
        var properties = new Dictionary<string, SubjectPropertyMetadata>();
        foreach (var name in names)
        {
            var propertyInfo = typeof(MimicValues).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
            properties[name] = new SubjectPropertyMetadata(
                propertyInfo,
                _ => null,
                (_, _) => { },
                isIntercepted: true,
                isDynamic: false);
        }

        return properties.ToFrozenDictionary();
    }
}

/// <summary>Carrier for the <see cref="PropertyInfo"/> instances the mimic metadata needs.</summary>
internal sealed class MimicValues
{
    public string RootValue { get; set; } = "";
    public string MiddleValue { get; set; } = "";
    public string LeafValue { get; set; } = "";
}

public class ChosenRoot : IInterceptorSubject
{
    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

    IInterceptorSubjectContext IInterceptorSubject.Context => throw new NotSupportedException();

    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

    object IInterceptorSubject.SyncRoot { get; } = new object();

    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        _properties = properties.ToDictionary(property => property.Name).ToFrozenDictionary();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
        MimicMetadata.Build(typeof(ChosenRoot), "RootValue");
}

public class ChosenMiddle : ChosenRoot, IInterceptorSubject
{
    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

    public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
        MimicMetadata.Build(typeof(ChosenMiddle), "RootValue", "MiddleValue");
}

public class ChosenLeaf : ChosenMiddle, IInterceptorSubject
{
    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

    public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
        MimicMetadata.Build(typeof(ChosenLeaf), "RootValue", "MiddleValue", "LeafValue");
}

public class AlternativeRoot : IInterceptorSubject
{
    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

    IInterceptorSubjectContext IInterceptorSubject.Context => throw new NotSupportedException();

    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? GetDefaultProperties();

    object IInterceptorSubject.SyncRoot { get; } = new object();

    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        _properties = properties.ToDictionary(property => property.Name).ToFrozenDictionary();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected virtual IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
        MimicMetadata.Build(typeof(AlternativeRoot), "RootValue");
}

public class AlternativeMiddle : AlternativeRoot
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

    public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
        MimicMetadata.Build(typeof(AlternativeMiddle), "RootValue", "MiddleValue");
}

public class AlternativeLeaf : AlternativeMiddle
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

    public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
        MimicMetadata.Build(typeof(AlternativeLeaf), "RootValue", "MiddleValue", "LeafValue");
}
