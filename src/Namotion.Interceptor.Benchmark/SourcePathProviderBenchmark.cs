using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SourcePathProviderBenchmark
{
    private Car _car;
    private PathProviderBase _pathProvider;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _car = new Car(context);
        _pathProvider = DefaultPathProvider.Instance;
    }

    [Benchmark]
    public void TryGetPropertyFromPath()
    {
        var (property, _) = _car.TryGetPropertyFromPath("Tires[1].Pressure", _pathProvider);
        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void VisitPropertiesFromPaths()
    {
        RegisteredSubjectProperty? property = null;
        _car.VisitPropertiesFromPaths(
            [
                "Tires[1].Pressure",
                "Tires[3].Pressure"
            ],
            (p, _, _) =>
            {
                property = p;
            }, _pathProvider);

        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void TryGetPropertyFromSegment()
    {
        var subject = _car.TryGetRegisteredSubject();
        var property = _pathProvider
            .TryGetPropertyFromSegment(subject ?? throw new InvalidOperationException(), "Name");

        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void TryGetPath()
    {
        var property = _car.Tires[1].TryGetRegisteredSubject()?.TryGetProperty("Pressure");
        var path = property!.TryGetPath(_pathProvider, null);
        if (path is null)
        {
            throw new InvalidOperationException();
        }
    }
}