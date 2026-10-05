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

    private void Resolve(string path)
    {
        if (_pathProvider.TryGetPropertyFromPath(_garage, path) is null)
        {
            throw new InvalidOperationException();
        }
    }
}
