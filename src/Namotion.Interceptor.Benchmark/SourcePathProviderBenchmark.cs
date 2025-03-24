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
    private Car _object;
    private IInterceptorSubjectContext? _context;
    
    [Params(
        "regular",
        "interceptor"
    )]
    public string? Type;

    [GlobalSetup]
    public void Setup()
    {
        switch (Type)
        {
            case "regular":
                _object = new Car();
                break;
            
            case "interceptor":
                _context = InterceptorSubjectContext
                    .Create()
                    .WithFullPropertyTracking()
                    .WithRegistry();

                _object = new Car(_context);
                break;
        }
    }

    [Benchmark]
    public void TryGetPropertyFromSegment()
    {
        var subject = _object.TryGetRegisteredSubject();
        var property = DefaultSourcePathProvider
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
        var property = _object.Tires[1].TryGetRegisteredSubject()?.TryGetProperty("Pressure");
        var path = property!.TryGetSourcePath(DefaultSourcePathProvider.Instance, null);
        if (path is null)
        {
            throw new InvalidOperationException();
        }
    }
}