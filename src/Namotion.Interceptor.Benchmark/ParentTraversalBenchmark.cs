using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[InterceptorSubject]
public partial class ParentTraversalNode
{
    public partial ParentTraversalNode? Child { get; set; }

    public partial ParentTraversalNode? OtherChild { get; set; }
}

[InterceptorSubject]
public partial class ParentTraversalMarkedNode
{
    public partial ParentTraversalNode? Child { get; set; }

    public partial ParentTraversalNode? OtherChild { get; set; }
}

[MemoryDiagnoser]
public class ParentTraversalBenchmark
{
    private const int Operations = 256;
    private const int ChainLength = 8;
    private const int ChainLengthBelowBranch = 4;
    private const int CycleLengthAboveSubject = 9;
    private const int LongChainLength = 64;

    private ParentTraversalNode _leafBelowMatch;
    private ParentTraversalNode _leafWithoutMatch;
    private ParentTraversalNode _nodeOnCycle;
    private ParentTraversalNode _leafBelowBranch;
    private ParentTraversalNode _nodeWithTwoParents;
    private ParentTraversalNode _nodeBelowCycle;
    private ParentTraversalNode _leafBelowChainIntoCycle;
    private ParentTraversalNode _leafBelowBranchWithoutMatch;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        var markedRoot = new ParentTraversalMarkedNode(context);
        var firstBelowMarkedRoot = new ParentTraversalNode();
        markedRoot.Child = firstBelowMarkedRoot;
        _leafBelowMatch = AppendChain(firstBelowMarkedRoot, ChainLength - 1);

        var plainRoot = new ParentTraversalNode(context);
        _leafWithoutMatch = AppendChain(plainRoot, ChainLength);

        var cycleStart = new ParentTraversalNode(context);
        var cycleEnd = AppendChain(cycleStart, ChainLength - 1);
        cycleEnd.Child = cycleStart;
        _nodeOnCycle = cycleStart;

        var branchRoot = new ParentTraversalMarkedNode(context);
        var left = new ParentTraversalNode();
        var right = new ParentTraversalNode();
        branchRoot.Child = left;
        branchRoot.OtherChild = right;
        var shared = new ParentTraversalNode();
        left.Child = shared;
        right.Child = shared;
        _nodeWithTwoParents = shared;
        _leafBelowBranch = AppendChain(shared, ChainLengthBelowBranch);

        var cycleAboveSubjectStart = new ParentTraversalNode(context);
        var cycleAboveSubjectEnd = AppendChain(cycleAboveSubjectStart, CycleLengthAboveSubject - 1);
        cycleAboveSubjectEnd.Child = cycleAboveSubjectStart;
        _nodeBelowCycle = new ParentTraversalNode();
        cycleAboveSubjectStart.OtherChild = _nodeBelowCycle;

        var ownParent = new ParentTraversalNode(context);
        ownParent.Child = ownParent;
        var firstBelowOwnParent = new ParentTraversalNode();
        ownParent.OtherChild = firstBelowOwnParent;
        _leafBelowChainIntoCycle = AppendChain(firstBelowOwnParent, LongChainLength - 1);

        var plainBranchRoot = new ParentTraversalNode(context);
        var plainLeft = new ParentTraversalNode();
        var plainRight = new ParentTraversalNode();
        plainBranchRoot.Child = plainLeft;
        plainBranchRoot.OtherChild = plainRight;
        var plainShared = new ParentTraversalNode();
        plainLeft.Child = plainShared;
        plainRight.Child = plainShared;
        _leafBelowBranchWithoutMatch = AppendChain(plainShared, ChainLengthBelowBranch);

        Ensure(CountOneParentSteps(_leafBelowMatch) == ChainLength, nameof(FindOnChain));
        Ensure(ReferenceEquals(_leafBelowMatch.TryGetFirstParent<ParentTraversalMarkedNode>(), markedRoot), nameof(FindOnChain));

        Ensure(CountOneParentSteps(_leafWithoutMatch) == ChainLength, nameof(MissOnChain));
        Ensure(_leafWithoutMatch.TryGetFirstParent<ParentTraversalMarkedNode>() is null, nameof(MissOnChain));

        Ensure(ReferenceEquals(StepUp(_nodeOnCycle, ChainLength), _nodeOnCycle), nameof(MissOnCycle));
        Ensure(_nodeOnCycle.TryGetFirstParent<ParentTraversalMarkedNode>() is null, nameof(MissOnCycle));

        Ensure(CountOneParentSteps(_leafBelowBranch) == ChainLengthBelowBranch, nameof(FindAboveBranch));
        Ensure(ReferenceEquals(_leafBelowBranch.TryGetFirstParent<ParentTraversalMarkedNode>(), branchRoot), nameof(FindAboveBranch));

        Ensure(_nodeWithTwoParents.GetParents().Length == 2, nameof(FindWithBranchAtSubject));
        Ensure(ReferenceEquals(_nodeWithTwoParents.TryGetFirstParent<ParentTraversalMarkedNode>(), branchRoot), nameof(FindWithBranchAtSubject));

        Ensure(ReferenceEquals(StepUp(_nodeBelowCycle, 1), cycleAboveSubjectStart), nameof(MissBelowCycle));
        Ensure(ReferenceEquals(StepUp(cycleAboveSubjectStart, CycleLengthAboveSubject), cycleAboveSubjectStart), nameof(MissBelowCycle));
        Ensure(_nodeBelowCycle.TryGetFirstParent<ParentTraversalMarkedNode>() is null, nameof(MissBelowCycle));

        Ensure(ReferenceEquals(StepUp(_leafBelowChainIntoCycle, LongChainLength), ownParent), nameof(MissBelowChainIntoCycle));
        Ensure(ReferenceEquals(StepUp(ownParent, 1), ownParent), nameof(MissBelowChainIntoCycle));
        Ensure(_leafBelowChainIntoCycle.TryGetFirstParent<ParentTraversalMarkedNode>() is null, nameof(MissBelowChainIntoCycle));

        Ensure(CountOneParentSteps(_leafBelowBranchWithoutMatch) == ChainLengthBelowBranch, nameof(MissAboveBranch));
        Ensure(plainShared.GetParents().Length == 2, nameof(MissAboveBranch));
        Ensure(_leafBelowBranchWithoutMatch.TryGetFirstParent<ParentTraversalMarkedNode>() is null, nameof(MissAboveBranch));
    }

    /// <summary>
    /// The match is the root of a chain of eight subjects with one parent each.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int FindOnChain() => CountMatches(_leafBelowMatch);

    /// <summary>
    /// The same chain without a match: the search ends at a subject without parents.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissOnChain() => CountMatches(_leafWithoutMatch);

    /// <summary>
    /// Eight subjects with one parent each that form a cycle through the subject, without a match.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissOnCycle() => CountMatches(_nodeOnCycle);

    /// <summary>
    /// The subject hangs below a cycle of nine subjects with one parent each, without a match.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissBelowCycle() => CountMatches(_nodeBelowCycle);

    /// <summary>
    /// Sixty-four subjects with one parent each below a subject that is its own parent, without a match.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissBelowChainIntoCycle() => CountMatches(_leafBelowChainIntoCycle);

    /// <summary>
    /// Four subjects with one parent each, then a subject with two parents, with the match above both.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int FindAboveBranch() => CountMatches(_leafBelowBranch);

    /// <summary>
    /// Four subjects with one parent each, then a subject with two parents, without a match above.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissAboveBranch() => CountMatches(_leafBelowBranchWithoutMatch);

    /// <summary>
    /// The subject itself has two parents, with the match above both.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int FindWithBranchAtSubject() => CountMatches(_nodeWithTwoParents);

    private static int CountMatches(ParentTraversalNode subject)
    {
        var matches = 0;
        for (var i = 0; i < Operations; i++)
        {
            if (subject.TryGetFirstParent<ParentTraversalMarkedNode>() is not null)
            {
                matches++;
            }
        }

        return matches;
    }

    private static ParentTraversalNode AppendChain(ParentTraversalNode start, int length)
    {
        var current = start;
        for (var i = 0; i < length; i++)
        {
            var next = new ParentTraversalNode();
            current.Child = next;
            current = next;
        }

        return current;
    }

    private static int CountOneParentSteps(IInterceptorSubject subject)
    {
        var steps = 0;
        var current = subject;
        while (steps < 100 && current.GetParents().Length == 1)
        {
            current = current.GetParents()[0].Property.Subject;
            steps++;
        }

        return steps;
    }

    private static IInterceptorSubject StepUp(IInterceptorSubject subject, int steps)
    {
        var current = subject;
        for (var i = 0; i < steps; i++)
        {
            var parents = current.GetParents();
            Ensure(parents.Length == 1, nameof(StepUp));
            current = parents[0].Property.Subject;
        }

        return current;
    }

    private static void Ensure(bool condition, string benchmark)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"The graph of '{benchmark}' is not what the benchmark measures.");
        }
    }
}
