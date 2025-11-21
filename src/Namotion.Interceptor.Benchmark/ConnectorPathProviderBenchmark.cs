using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class ConnectorPathProviderBenchmark
{
    private Car _car;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _car = new Car(context);
    }

    [Benchmark]
    public void TryGetPropertyFromSourcePath()
    {
        var (property, _) = _car.TryGetPropertyFromSourcePath("Tires[1].Pressure", DefaultConnectorPathProvider.Instance);
        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void VisitPropertiesFromSourcePaths()
    {
        RegisteredSubjectProperty? property = null;
        _car.VisitPropertiesFromSourcePaths(
            [
                "Tires[1].Pressure",
                "Tires[3].Pressure"
            ],
            (p, _, _) =>
            {
                property = p;
            }, DefaultConnectorPathProvider.Instance);

        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void TryGetPropertyFromSegment()
    {
        var subject = _car.TryGetRegisteredSubject();
        var property = DefaultConnectorPathProvider
            .Instance
            .TryGetPropertyFromSegment(subject ?? throw new InvalidOperationException(), "Name");

        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    [Benchmark]
    public void TryGetSourcePath()
    {
        var property = _car.Tires[1].TryGetRegisteredSubject()?.TryGetProperty("Pressure");
        var path = property!.TryGetSourcePath(DefaultConnectorPathProvider.Instance, null);
        if (path is null)
        {
            throw new InvalidOperationException();
        }
    }
}