using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class BenchmarkRoot
{
    public partial string RootValue { get; set; }

    public BenchmarkRoot()
    {
        RootValue = "";
    }
}

[InterceptorSubject]
public partial class BenchmarkMiddle : BenchmarkRoot
{
    public partial string MiddleValue { get; set; }

    public BenchmarkMiddle()
    {
        MiddleValue = "";
    }
}

[InterceptorSubject]
public partial class BenchmarkLeaf : BenchmarkMiddle
{
    public partial string LeafValue { get; set; }

    public BenchmarkLeaf()
    {
        LeafValue = "";
    }
}

[MemoryDiagnoser]
public class SubjectHierarchyBenchmark
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking();

    private BenchmarkRoot _root = null!;
    private BenchmarkLeaf _leaf = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = new BenchmarkRoot(_context);
        _leaf = new BenchmarkLeaf(_context);
    }

    [Benchmark] public string RootOnlyGet() => _root.RootValue;
    [Benchmark] public void RootOnlySet() => _root.RootValue = "x";
    [Benchmark] public string DerivedDeclaredGet() => _leaf.LeafValue;
    [Benchmark] public void DerivedDeclaredSet() => _leaf.LeafValue = "x";
    [Benchmark] public int PropertiesAccess() => ((IInterceptorSubject)_leaf).Properties.Count;
    [Benchmark] public BenchmarkLeaf ConstructThreeLevel() => new(_context);

    // Not a gate. This row is the one the spec's rejected alternative would change: it is here so
    // the rejection rests on a number rather than on reasoning.
    [Benchmark] public string BaseDeclaredSetThenGet()
    {
        _leaf.RootValue = "x";
        return _leaf.RootValue;
    }
}
