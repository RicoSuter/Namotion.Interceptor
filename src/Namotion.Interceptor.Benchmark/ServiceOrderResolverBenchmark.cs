using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Benchmark;

/// <summary>An unrelated service-ordering row used as a timing-noise reference.</summary>
[MemoryDiagnoser]
public class ServiceOrderResolverBenchmark
{
    private object[] _servicesWithChain = null!;

    [GlobalSetup]
    public void Setup()
    {
        _servicesWithChain =
        [
            new ChainService15(), new ChainService14(), new ChainService13(), new ChainService12(),
            new ChainService11(), new ChainService10(), new ChainService09(), new ChainService08(),
            new ChainService07(), new ChainService06(), new ChainService05(), new ChainService04(),
            new ChainService03(), new ChainService02(), new ChainService01()
        ];
    }

    [Benchmark]
    public object[] LinearChain()
    {
        return ServiceOrderResolver.OrderByDependencies(_servicesWithChain);
    }

    [RunsBefore(typeof(ChainService02))]
    private class ChainService01 { }

    [RunsBefore(typeof(ChainService03))]
    private class ChainService02 { }

    [RunsBefore(typeof(ChainService04))]
    private class ChainService03 { }

    [RunsBefore(typeof(ChainService05))]
    private class ChainService04 { }

    [RunsBefore(typeof(ChainService06))]
    private class ChainService05 { }

    [RunsBefore(typeof(ChainService07))]
    private class ChainService06 { }

    [RunsBefore(typeof(ChainService08))]
    private class ChainService07 { }

    [RunsBefore(typeof(ChainService09))]
    private class ChainService08 { }

    [RunsBefore(typeof(ChainService10))]
    private class ChainService09 { }

    [RunsBefore(typeof(ChainService11))]
    private class ChainService10 { }

    [RunsBefore(typeof(ChainService12))]
    private class ChainService11 { }

    [RunsBefore(typeof(ChainService13))]
    private class ChainService12 { }

    [RunsBefore(typeof(ChainService14))]
    private class ChainService13 { }

    [RunsBefore(typeof(ChainService15))]
    private class ChainService14 { }

    private class ChainService15 { }
}
