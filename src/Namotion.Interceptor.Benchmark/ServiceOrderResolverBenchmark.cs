using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// Benchmarks for ServiceOrderResolver.OrderByDependencies with various service configurations.
/// </summary>
[MemoryDiagnoser]
public class ServiceOrderResolverBenchmark
{
    private object[] _servicesNoDependencies = null!;
    private object[] _servicesWithChain = null!;
    private object[] _servicesWithMixedDependencies = null!;
    private object[] _servicesWithFirstLast = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 15 services with no dependencies
        _servicesNoDependencies =
        [
            new Service01(), new Service02(), new Service03(), new Service04(), new Service05(),
            new Service06(), new Service07(), new Service08(), new Service09(), new Service10(),
            new Service11(), new Service12(), new Service13(), new Service14(), new Service15()
        ];

        // 15 services with a dependency chain: S1 -> S2 -> S3 -> ... -> S15
        _servicesWithChain =
        [
            new ChainService15(), new ChainService14(), new ChainService13(), new ChainService12(),
            new ChainService11(), new ChainService10(), new ChainService09(), new ChainService08(),
            new ChainService07(), new ChainService06(), new ChainService05(), new ChainService04(),
            new ChainService03(), new ChainService02(), new ChainService01()
        ];

        // 15 services with mixed dependencies (some chains, some independent)
        _servicesWithMixedDependencies =
        [
            new MixedService01(), new MixedService02(), new MixedService03(), new MixedService04(),
            new MixedService05(), new MixedService06(), new MixedService07(), new MixedService08(),
            new MixedService09(), new MixedService10(), new MixedService11(), new MixedService12(),
            new MixedService13(), new MixedService14(), new MixedService15()
        ];

        // 15 services with RunsFirst/RunsLast grouping
        _servicesWithFirstLast =
        [
            new GroupedService01(), new GroupedService02(), new GroupedService03(),
            new GroupedService04(), new GroupedService05(), new GroupedService06(),
            new GroupedService07(), new GroupedService08(), new GroupedService09(),
            new GroupedService10(), new GroupedService11(), new GroupedService12(),
            new GroupedService13(), new GroupedService14(), new GroupedService15()
        ];
    }

    [Benchmark(Baseline = true)]
    public object[] NoDependencies()
    {
        return ServiceOrderResolver.OrderByDependencies(_servicesNoDependencies);
    }

    [Benchmark]
    public object[] LinearChain()
    {
        return ServiceOrderResolver.OrderByDependencies(_servicesWithChain);
    }

    [Benchmark]
    public object[] MixedDependencies()
    {
        return ServiceOrderResolver.OrderByDependencies(_servicesWithMixedDependencies);
    }

    [Benchmark]
    public object[] WithFirstLastGroups()
    {
        return ServiceOrderResolver.OrderByDependencies(_servicesWithFirstLast);
    }

    #region Test services - No dependencies

    private class Service01 { }
    private class Service02 { }
    private class Service03 { }
    private class Service04 { }
    private class Service05 { }
    private class Service06 { }
    private class Service07 { }
    private class Service08 { }
    private class Service09 { }
    private class Service10 { }
    private class Service11 { }
    private class Service12 { }
    private class Service13 { }
    private class Service14 { }
    private class Service15 { }

    #endregion

    #region Test services - Linear chain (worst case for topological sort)

    // Chain: 01 -> 02 -> 03 -> ... -> 15
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

    #endregion

    #region Test services - Mixed dependencies

    // Three chains: 01->02->03, 04->05->06, 07->08->09
    // Plus independent: 10, 11, 12, 13, 14, 15
    [RunsBefore(typeof(MixedService02))]
    private class MixedService01 { }

    [RunsBefore(typeof(MixedService03))]
    private class MixedService02 { }

    private class MixedService03 { }

    [RunsBefore(typeof(MixedService05))]
    private class MixedService04 { }

    [RunsBefore(typeof(MixedService06))]
    private class MixedService05 { }

    private class MixedService06 { }

    [RunsBefore(typeof(MixedService08))]
    private class MixedService07 { }

    [RunsBefore(typeof(MixedService09))]
    private class MixedService08 { }

    private class MixedService09 { }

    private class MixedService10 { }
    private class MixedService11 { }
    private class MixedService12 { }
    private class MixedService13 { }
    private class MixedService14 { }
    private class MixedService15 { }

    #endregion

    #region Test services - With RunsFirst/RunsLast groups

    // First group: 01, 02, 03 (with internal ordering)
    // Middle group: 04-12 (no special attributes)
    // Last group: 13, 14, 15 (with internal ordering)

    [RunsFirst]
    [RunsBefore(typeof(GroupedService02))]
    private class GroupedService01 { }

    [RunsFirst]
    [RunsBefore(typeof(GroupedService03))]
    private class GroupedService02 { }

    [RunsFirst]
    private class GroupedService03 { }

    private class GroupedService04 { }
    private class GroupedService05 { }
    private class GroupedService06 { }
    private class GroupedService07 { }
    private class GroupedService08 { }
    private class GroupedService09 { }
    private class GroupedService10 { }
    private class GroupedService11 { }
    private class GroupedService12 { }

    [RunsLast]
    private class GroupedService13 { }

    [RunsLast]
    [RunsAfter(typeof(GroupedService13))]
    private class GroupedService14 { }

    [RunsLast]
    [RunsAfter(typeof(GroupedService14))]
    private class GroupedService15 { }

    #endregion
}
