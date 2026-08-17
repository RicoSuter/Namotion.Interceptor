using System.Linq;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>Constructs and registers a child graph through one structural property write.</summary>
[MemoryDiagnoser]
public class RegistryBenchmark
{
    private Car _object;

    [Params(4, 1000)]
    public int Count;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _object = new Car(context);
    }

    [Benchmark]
    public void AddLotsOfPreviousCars()
    {
        _object.PreviousCars = Enumerable.Range(0, Count)
            .Select(_ => new Car())
            .ToArray();
    }
}
