using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Reads the Registry and parent-tracking projections of a subject's incoming relationships side by side.
/// Both calls exercise their cached hot paths after setup has materialized the snapshots.
/// </summary>
[MemoryDiagnoser]
public class ParentLookupBenchmark
{
    private Tire _tire;
    private RegisteredSubject _registered;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents()
            .WithRegistry();

        var car = new Car(context);
        _tire = car.Tires[0];
        _registered = _tire.TryGetRegisteredSubject()!;

        _ = _registered.Parents;
        _ = _tire.GetParents();
    }

    /// <summary>The registry's copy, behind a lock-free snapshot.</summary>
    [Benchmark(OperationsPerInvoke = 256)]
    public int ReadRegistryParents()
    {
        var sum = 0;
        for (var i = 0; i < 256; i++)
        {
            sum += _registered.Parents.Length;
        }

        return sum;
    }

    /// <summary>The tracked copy, reached through the subject's data bag.</summary>
    [Benchmark(OperationsPerInvoke = 256)]
    public int ReadTrackedParents()
    {
        var sum = 0;
        for (var i = 0; i < 256; i++)
        {
            sum += _tire.GetParents().Length;
        }

        return sum;
    }
}
