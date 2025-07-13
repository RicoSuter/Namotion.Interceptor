using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[MemoryDiagnoser]
public class SourcePathProviderBenchmark
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

    // [Benchmark]
    public void TryGetPropertyFromSegment()
    {
        var subject = _car.TryGetRegisteredSubject();
        var property = DefaultSourcePathProvider
            .Instance
            .TryGetPropertyFromSegment(subject ?? throw new InvalidOperationException(), "Name");
        
        if (property is null)
        {
            throw new InvalidOperationException();
        }
    }

    // [Benchmark]
    public void TryGetSourcePath()
    {
        var property = _car.Tires[1].TryGetRegisteredSubject()?.TryGetProperty("Pressure");
        var path = property!.TryGetSourcePath(DefaultSourcePathProvider.Instance, null);
        if (path is null)
        {
            throw new InvalidOperationException();
        }
    }
}