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
    private const int ChainLengthPastBuffer = 16;
    private const int ManyParents = 20;
    private const int LongChainLength = 64;

    private ParentTraversalNode _leafBelowMatch;
    private ParentTraversalNode _nodeWithTwoParents;
    private ParentTraversalNode _leafOfChainPastBuffer;
    private ParentTraversalNode _nodeWithManyParents;
    private ParentTraversalNode _leafBelowChainIntoCycle;

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

        var branchRoot = new ParentTraversalMarkedNode(context);
        var left = new ParentTraversalNode();
        var right = new ParentTraversalNode();
        branchRoot.Child = left;
        branchRoot.OtherChild = right;
        _nodeWithTwoParents = new ParentTraversalNode();
        left.Child = _nodeWithTwoParents;
        right.Child = _nodeWithTwoParents;

        _leafOfChainPastBuffer = AppendChain(new ParentTraversalNode(context), ChainLengthPastBuffer);

        _nodeWithManyParents = new ParentTraversalNode();
        var lastOfManyParents = new ParentTraversalNode(context);
        for (var i = 0; i < ManyParents; i++)
        {
            lastOfManyParents = new ParentTraversalNode(context);
            lastOfManyParents.Child = _nodeWithManyParents;
        }

        var rootAboveLastParent = new ParentTraversalMarkedNode(context);
        rootAboveLastParent.Child = lastOfManyParents;

        var ownParent = new ParentTraversalNode(context);
        ownParent.Child = ownParent;
        var firstBelowOwnParent = new ParentTraversalNode();
        ownParent.OtherChild = firstBelowOwnParent;
        _leafBelowChainIntoCycle = AppendChain(firstBelowOwnParent, LongChainLength - 1);

        Ensure(ReferenceEquals(Find(_leafBelowMatch), markedRoot), nameof(FindOnChain));
        Ensure(ReferenceEquals(Find(_nodeWithTwoParents), branchRoot), nameof(FindWithBranchAtSubject));
        Ensure(Find(_leafOfChainPastBuffer) is null, nameof(MissOnChainPastBuffer));
        Ensure(ReferenceEquals(_nodeWithManyParents.GetParents()[ManyParents - 1].Property.Subject, lastOfManyParents), nameof(FindAboveManyParents));
        Ensure(ReferenceEquals(Find(_nodeWithManyParents), rootAboveLastParent), nameof(FindAboveManyParents));
        Ensure(Find(_leafBelowChainIntoCycle) is null, nameof(MissBelowChainIntoCycle));
    }

    /// <summary>
    /// The match is the root of a chain of eight subjects with one parent each.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int FindOnChain() => CountMatches(_leafBelowMatch);

    /// <summary>
    /// The subject itself has two parents, with the match above both.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int FindWithBranchAtSubject() => CountMatches(_nodeWithTwoParents);

    /// <summary>
    /// Sixteen subjects with one parent each above the subject, without a match: one subject more than
    /// the search keeps on the stack.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissOnChainPastBuffer() => CountMatches(_leafOfChainPastBuffer);

    /// <summary>
    /// The subject has twenty parents, with the match above the last of them.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int FindAboveManyParents() => CountMatches(_nodeWithManyParents);

    /// <summary>
    /// Sixty-four subjects with one parent each below a subject that is its own parent, without a match.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Operations)]
    public int MissBelowChainIntoCycle() => CountMatches(_leafBelowChainIntoCycle);

    private static int CountMatches(ParentTraversalNode subject)
    {
        var matches = 0;
        for (var i = 0; i < Operations; i++)
        {
            if (Find(subject) is not null)
            {
                matches++;
            }
        }

        return matches;
    }

    private static ParentTraversalMarkedNode? Find(ParentTraversalNode subject)
    {
        return subject.TryGetFirstParent<ParentTraversalMarkedNode>();
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

    private static void Ensure(bool condition, string benchmark)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"The graph of '{benchmark}' is not what the benchmark measures.");
        }
    }
}
