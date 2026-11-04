using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class FanInRoot
{
    public FanInRoot()
    {
        Children = [];
    }

    public partial bool Gate { get; set; }

    public partial bool Source { get; set; }

    public partial FanInChild[] Children { get; set; }
}

[InterceptorSubject]
public partial class FanInChild
{
    // Not intercepted: the child only reads the root's properties.
    public FanInRoot Root { get; init; } = null!;

    [Derived]
    public bool IsActive => Root.Gate && Root.Source;
}

/// <summary>
/// Many derived properties reading the same properties, as when every derived property in a subtree
/// reads one branch-level state. Measures how the used-by tracking of the shared properties scales
/// with the number of dependents.
/// </summary>
[MemoryDiagnoser]
[MaxWarmupCount(8)]
[MaxIterationCount(20)]
public class DerivedFanInBenchmark
{
    private FanInRoot _root = null!;
    private FanInChild[] _children = null!;

    [Params(1, 8, 32, 100, 1_000, 10_000, 50_000)]
    public int DependentCount;

    [GlobalSetup(Target = nameof(AttachDetach))]
    public void SetupDetached()
    {
        Setup();
    }

    [GlobalSetup(Targets = [nameof(Change), nameof(Regate)])]
    public void SetupAttached()
    {
        Setup();
        _root.Children = _children;

        var dependentCount = new PropertyReference(_root, nameof(FanInRoot.Source)).GetUsedByProperties().Count;
        if (dependentCount != DependentCount)
        {
            throw new InvalidOperationException($"Expected {DependentCount} dependents of Source but found {dependentCount}.");
        }
    }

    private void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithContextInheritance();

        _root = new FanInRoot(context) { Gate = true, Source = true };
        _children = new FanInChild[DependentCount];
        for (var index = 0; index < _children.Length; index++)
        {
            _children[index] = new FanInChild { Root = _root };
        }
    }

    /// <summary>
    /// Records every dependent on attach and removes it again on detach.
    /// </summary>
    [Benchmark]
    public void AttachDetach()
    {
        _root.Children = _children;
        _root.Children = [];
    }

    /// <summary>
    /// Recalculates every dependent with an unchanged dependency set.
    /// </summary>
    [Benchmark]
    public void Change()
    {
        _root.Source = false;
        _root.Source = true;
    }

    /// <summary>
    /// Recalculates every dependent, each of which drops Source from its dependencies and records it again.
    /// </summary>
    [Benchmark]
    public void Regate()
    {
        _root.Gate = false;
        _root.Gate = true;
    }
}
