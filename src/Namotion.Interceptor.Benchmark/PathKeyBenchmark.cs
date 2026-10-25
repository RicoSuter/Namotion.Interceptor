using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class PathKeyBenchmark
{
    private RegisteredSubject _garage;
    private PathProviderBase _pathProvider;
    private RegisteredSubjectProperty _inlineNameProperty;
    private RegisteredSubject _mappedGarage;
    private AttributeBasedPathProvider _attributePathProvider;
    private RegisteredSubjectProperty _mappedInlineNameProperty;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var garage = new Garage(context)
        {
            ByName = new Dictionary<string, Car> { ["alpha"] = new Car() },
            ById = new Dictionary<int, Car> { [7] = new Car() },
            Spots = new Dictionary<string, Car> { ["A1"] = new Car() }
        };

        _garage = garage.TryGetRegisteredSubject()!;
        _pathProvider = DefaultPathProvider.Instance;
        _inlineNameProperty = garage.Spots["A1"].TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        var mappedGarage = new MappedGarage(context)
        {
            Spots = new Dictionary<string, MappedCar> { ["A1"] = new MappedCar() }
        };

        _mappedGarage = mappedGarage.TryGetRegisteredSubject()!;
        _attributePathProvider = new AttributeBasedPathProvider("mqtt");
        _mappedInlineNameProperty = mappedGarage.Spots["A1"].TryGetRegisteredSubject()!.TryGetProperty("Name")!;
    }

    [Benchmark]
    public void TryGetPropertyFromPath_StringKey() => Resolve("ByName[alpha].Name");

    [Benchmark]
    public void TryGetPropertyFromPath_IntKey() => Resolve("ById[7].Name");

    [Benchmark]
    public void TryGetPropertyFromPath_InlineKey() => Resolve("A1.Name");

    [Benchmark]
    public void TryGetPath_InlineKey()
    {
        if (_inlineNameProperty.TryGetPath(_pathProvider, null) is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void TryGetPropertyFromPath_CollectionPosition() => Resolve("ByName[alpha].Tires[1].Pressure");

    [Benchmark]
    public void TryGetPath_InlineKey_AttributeProvider()
    {
        if (_mappedInlineNameProperty.TryGetPath(_attributePathProvider, null) is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void TryGetPropertyFromPath_InlineKey_AttributeProvider()
    {
        if (_attributePathProvider.TryGetPropertyFromPath(_mappedGarage, "A1.name") is null)
        {
            throw new InvalidOperationException();
        }
    }

    private void Resolve(string path)
    {
        if (_pathProvider.TryGetPropertyFromPath(_garage, path) is null)
        {
            throw new InvalidOperationException();
        }
    }
}
