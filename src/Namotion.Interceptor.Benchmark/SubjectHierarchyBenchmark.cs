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
    private IInterceptorSubject[] _hierarchy = null!;
    private PropertyReference _baseDeclaredReference;
    private Car _car = null!;
    private Tire _tire = null!;
    private bool _isAlternate;

    [GlobalSetup]
    public void Setup()
    {
        _root = new BenchmarkRoot(_context);
        _leaf = new BenchmarkLeaf(_context);
        _hierarchy = [_root, new BenchmarkMiddle(_context), _leaf];
        _baseDeclaredReference = new PropertyReference(_leaf, nameof(BenchmarkRoot.RootValue));
        _car = new Car(_context);
        _tire = _car.Tires[0];
    }

    [Benchmark] public string RootOnlyGet() => _root.RootValue;
    [Benchmark] public void RootOnlySet() => _root.RootValue = "x";

    // Writes to one type without derived property data, with alternating values so that the equality check lets
    // every write land and reach the derived type gate.
    [Benchmark]
    public void RootOnlyLandedSet()
    {
        _isAlternate = !_isAlternate;
        _root.RootValue = _isAlternate ? "x" : "y";
    }

    // Writes alternate between subject types, as in a real graph, so the thread's last derived type gate never
    // matches: a type without derived property data (BenchmarkRoot) and two types a derived property read (Car
    // and Tire, through Car.AveragePressure), each on a property no derived property reads. The values alternate
    // so that the equality check lets every write land.
    [Benchmark]
    public void MixedTypeSet()
    {
        _isAlternate = !_isAlternate;
        _root.RootValue = _isAlternate ? "x" : "y";
        _car.Name = _isAlternate ? "x" : "y";
        _tire.Pressure_Minimum = _isAlternate ? 1m : 0m;
    }

    [Benchmark] public string DerivedDeclaredGet() => _leaf.LeafValue;
    [Benchmark] public void DerivedDeclaredSet() => _leaf.LeafValue = "x";
    [Benchmark] public int PropertiesAccess() => ((IInterceptorSubject)_leaf).Properties.Count;
    // Without a context, as subjects that inherit one on attach are constructed. Passing _context would
    // register each subject with it, keeping every subject ever constructed alive.
    [Benchmark] public BenchmarkLeaf ConstructThreeLevel() => new();

    // The representative shape for Properties, and the reason the row above is not sufficient on its
    // own: PropertyReference.Metadata is one member in Namotion.Interceptor, so its Subject.Properties
    // read is a single call site that every subject type in the process passes through. Reading one
    // field of one static type lets the JIT devirtualize what production never can.
    [Benchmark]
    public int PropertiesAccessPolymorphic()
    {
        var total = 0;
        foreach (var subject in _hierarchy)
        {
            total += subject.Properties.Count;
        }

        return total;
    }

    // The uncached lookup itself, on a base-declared property reached from the leaf. Every intercepted
    // write pays this, because PropertyReference.Metadata resolves it on each access.
    [Benchmark] public string BaseDeclaredMetadataLookup() => _baseDeclaredReference.Metadata.Name;

    // Not a gate. This row is the one the spec's rejected alternative would change: it is here so
    // the rejection rests on a number rather than on reasoning.
    [Benchmark] public string BaseDeclaredSetThenGet()
    {
        _leaf.RootValue = "x";
        return _leaf.RootValue;
    }
}
