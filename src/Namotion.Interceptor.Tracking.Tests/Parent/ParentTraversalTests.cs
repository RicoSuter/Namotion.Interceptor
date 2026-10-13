using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

public class ParentTraversalTests
{
    private const int MeasuredSearches = 1000;

    [Fact]
    public void WhenParentIsValueEqualToChild_ThenFindsDistinctParent()
    {
        // Arrange
        var child = new ValueEqualParentSubject { Key = 1 };
        var parent = new ValueEqualParentSubject { Key = 1 };
        child.AddParent(new PropertyReference(parent, nameof(ValueEqualParentSubject.Child)), null);

        // Act
        var result = child.TryGetFirstParent<ValueEqualParentSubject>();

        // Assert
        Assert.Same(parent, result);
    }

    [Fact]
    public void WhenIntermediateParentsAreValueEqual_ThenFindsAncestorBeyondBoth()
    {
        // Arrange
        var child = new Person();
        var parent = new ValueEqualParentSubject { Key = 1 };
        var grandparent = new ValueEqualParentSubject { Key = 1 };
        var ancestor = new Person();
        child.AddParent(new PropertyReference(parent, nameof(ValueEqualParentSubject.Child)), null);
        parent.AddParent(new PropertyReference(grandparent, nameof(ValueEqualParentSubject.Child)), null);
        grandparent.AddParent(new PropertyReference(ancestor, nameof(Person.Children)), 0);

        // Act
        var result = child.TryGetFirstParent<Person>();

        // Assert
        Assert.Same(ancestor, result);
    }

    [Fact]
    public void WhenOnlyMatchingSubjectIsSelfInCycle_ThenReturnsNull()
    {
        // Arrange
        var child = new Person();
        var parent = new ValueEqualParentSubject { Key = 1 };
        var grandparent = new ValueEqualParentSubject { Key = 1 };
        child.AddParent(new PropertyReference(parent, nameof(ValueEqualParentSubject.Child)), null);
        parent.AddParent(new PropertyReference(grandparent, nameof(ValueEqualParentSubject.Child)), null);
        grandparent.AddParent(new PropertyReference(child, nameof(Person.Children)), 0);

        // Act
        var result = child.TryGetFirstParent<Person>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenTwoMatchesAreOnOneChain_ThenReturnsTheNearer()
    {
        // Arrange
        var subject = new TraversalNode();
        var nearer = new MarkedTraversalNode();
        var between = new TraversalNode();
        var farther = new MarkedTraversalNode();
        ParentTraversalGraph.Link(subject, nearer);
        ParentTraversalGraph.Link(nearer, between);
        ParentTraversalGraph.Link(between, farther);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(nearer, result);
    }

    [Fact]
    public void WhenMatchIsTwoHundredLevelsUp_ThenFindsIt()
    {
        // Arrange
        var subject = new TraversalNode();
        var topOfChain = ParentTraversalGraph.LinkChain(subject, 199);
        var root = new MarkedTraversalNode();
        ParentTraversalGraph.Link(topOfChain, root);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(root, result);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void WhenMatchIsAboutSixteenLevelsUp_ThenFindsIt(int levels)
    {
        // Arrange
        var subject = new TraversalNode();
        var topOfChain = ParentTraversalGraph.LinkChain(subject, levels - 1);
        var root = new MarkedTraversalNode();
        ParentTraversalGraph.Link(topOfChain, root);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(root, result);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(6, false)]
    [InlineData(8, true)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(9, false)]
    [InlineData(20, true)]
    [InlineData(20, false)]
    public void WhenTwoLongBranchesAreSearchedSideBySide_ThenFindsMatchAboveEither(
        int branchLength, bool matchIsAboveFirstBranch)
    {
        // Arrange
        var subject = new TraversalNode();
        var topOfFirstBranch = ParentTraversalGraph.LinkChain(subject, branchLength);
        var firstOfSecondBranch = new TraversalNode();
        ParentTraversalGraph.Link(subject, firstOfSecondBranch);
        var topOfSecondBranch = ParentTraversalGraph.LinkChain(firstOfSecondBranch, branchLength - 1);
        var match = new MarkedTraversalNode();
        ParentTraversalGraph.Link(matchIsAboveFirstBranch ? topOfFirstBranch : topOfSecondBranch, match);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(match, result);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(30)]
    public void WhenSubjectHasManyParents_ThenFindsMatchAboveTheLast(int parentCount)
    {
        // Arrange
        var subject = new TraversalNode();
        IInterceptorSubject lastParent = subject;
        for (var i = 0; i < parentCount; i++)
        {
            lastParent = new TraversalNode();
            ParentTraversalGraph.Link(subject, lastParent);
        }

        var match = new MarkedTraversalNode();
        ParentTraversalGraph.Link(lastParent, match);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(match, result);
    }

    [Fact]
    public void WhenParentEntryIsDefault_ThenItIsSkipped()
    {
        // Arrange
        var subject = new TraversalNode();
        subject.AddParent(default, null);
        var match = new MarkedTraversalNode();
        ParentTraversalGraph.Link(subject, match);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(match, result);
    }

    [Fact]
    public void WhenParentEntryIsDefaultAfterSeventeenParents_ThenItIsSkipped()
    {
        // Arrange
        var subject = new TraversalNode();
        for (var i = 0; i < 17; i++)
        {
            ParentTraversalGraph.Link(subject, new TraversalNode());
        }

        subject.AddParent(default, null);
        var parentBelowMatch = new TraversalNode();
        ParentTraversalGraph.Link(subject, parentBelowMatch);
        var match = new MarkedTraversalNode();
        ParentTraversalGraph.Link(parentBelowMatch, match);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(match, result);
    }

    [Fact]
    public async Task WhenSubjectIsItsOwnOnlyParent_ThenReturnsNull()
    {
        // Arrange
        var subject = new MarkedTraversalNode();
        ParentTraversalGraph.Link(subject, subject);

        // Act
        var result = await ParentTraversalGraph.FindMarkerWithinTimeoutAsync(subject);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task WhenMatchingSubjectIsOnCycleWithoutOtherMatch_ThenReturnsNull(int cycleLength)
    {
        // Arrange
        var subject = new MarkedTraversalNode();
        var lastOfCycle = ParentTraversalGraph.LinkChain(subject, cycleLength - 1);
        ParentTraversalGraph.Link(lastOfCycle, subject);

        // Act
        var result = await ParentTraversalGraph.FindMarkerWithinTimeoutAsync(subject);

        // Assert
        Assert.Null(result);
    }

    public static TheoryData<int, int> CycleAndTailLengths()
    {
        var data = new TheoryData<int, int>();
        foreach (var cycleLength in new[] { 1, 2, 3, 4, 5, 8, 9, 15, 16, 17, 33 })
        {
            for (var tailLength = 0; tailLength <= 4; tailLength++)
            {
                data.Add(cycleLength, tailLength);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CycleAndTailLengths))]
    public async Task WhenChainEndsInCycleWithoutMatch_ThenReturnsNull(int cycleLength, int tailLength)
    {
        // Arrange
        var subject = new TraversalNode();
        ParentTraversalGraph.LinkTailAndCycle(subject, tailLength, cycleLength);

        // Act
        var result = await ParentTraversalGraph.FindMarkerWithinTimeoutAsync(subject);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task WhenMatchIsInsideCycle_ThenFindsIt(int markedCyclePosition)
    {
        // Arrange
        var subject = new TraversalNode();
        var cycle = ParentTraversalGraph.LinkTailAndCycle(
            subject, tailLength: 3, cycleLength: 5, markedCyclePosition);

        // Act
        var result = await ParentTraversalGraph.FindMarkerWithinTimeoutAsync(subject);

        // Assert
        Assert.Same(cycle[markedCyclePosition], result);
    }

    [Fact]
    public void WhenChainReachesSubjectWithTwoParents_ThenFindsMatchAboveSecondParent()
    {
        // Arrange
        var subject = new TraversalNode();
        var branching = ParentTraversalGraph.LinkChain(subject, 2);
        var deadEnd = new TraversalNode();
        var second = new TraversalNode();
        var match = new MarkedTraversalNode();
        ParentTraversalGraph.Link(branching, deadEnd);
        ParentTraversalGraph.Link(branching, second);
        ParentTraversalGraph.Link(second, match);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(match, result);
    }

    [Fact]
    public void WhenTwoBranchesHaveMatchesAtDifferentDistances_ThenReturnsTheNearer()
    {
        // Arrange
        var subject = new TraversalNode();
        var longBranch = new TraversalNode();
        var shortBranch = new TraversalNode();
        var farther = new MarkedTraversalNode();
        var nearer = new MarkedTraversalNode();
        ParentTraversalGraph.Link(subject, longBranch);
        ParentTraversalGraph.Link(subject, shortBranch);
        var topOfLongBranch = ParentTraversalGraph.LinkChain(longBranch, 1);
        ParentTraversalGraph.Link(topOfLongBranch, farther);
        ParentTraversalGraph.Link(shortBranch, nearer);

        // Act
        var result = subject.TryGetFirstParent<ITraversalMarker>();

        // Assert
        Assert.Same(nearer, result);
    }

    [Fact]
    public async Task WhenBranchLeadsOnlyBackToMatchingSubject_ThenReturnsNull()
    {
        // Arrange
        var subject = new MarkedTraversalNode();
        var branching = ParentTraversalGraph.LinkChain(subject, 2);
        var deadEnd = new TraversalNode();
        ParentTraversalGraph.Link(branching, deadEnd);
        ParentTraversalGraph.Link(branching, subject);

        // Act
        var result = await ParentTraversalGraph.FindMarkerWithinTimeoutAsync(subject);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenMatchIsOnOneParentChain_ThenSearchDoesNotAllocate()
    {
        // Arrange
        var subject = new TraversalNode();
        var topOfChain = ParentTraversalGraph.LinkChain(subject, 7);
        ParentTraversalGraph.Link(topOfChain, new MarkedTraversalNode());

        // Act
        var (allocated, matches) = MeasureSearch(subject);

        // Assert
        Assert.Equal(MeasuredSearches, matches);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void WhenOneParentChainHasNoMatch_ThenSearchDoesNotAllocate()
    {
        // Arrange
        var subject = new TraversalNode();
        ParentTraversalGraph.LinkChain(subject, 8);

        // Act
        var (allocated, matches) = MeasureSearch(subject);

        // Assert
        Assert.Equal(0, matches);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void WhenOneParentChainEndsInCycleWithoutMatch_ThenSearchDoesNotAllocate()
    {
        // Arrange
        var subject = new TraversalNode();
        ParentTraversalGraph.LinkTailAndCycle(subject, tailLength: 2, cycleLength: 5);

        // Act
        var (allocated, matches) = MeasureSearch(subject);

        // Assert
        Assert.Equal(0, matches);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void WhenSubjectHasTwoParents_ThenSearchDoesNotAllocate()
    {
        // Arrange
        var subject = new TraversalNode();
        var first = new TraversalNode();
        var second = new TraversalNode();
        ParentTraversalGraph.Link(subject, first);
        ParentTraversalGraph.Link(subject, second);
        var match = new MarkedTraversalNode();
        ParentTraversalGraph.Link(first, match);
        ParentTraversalGraph.Link(second, match);

        // Act
        var (allocated, matches) = MeasureSearch(subject);

        // Assert
        Assert.Equal(MeasuredSearches, matches);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void WhenSearchFindsSixteenSubjects_ThenItDoesNotAllocate()
    {
        // Arrange
        var subject = new TraversalNode();
        ParentTraversalGraph.LinkChain(subject, 15);

        // Act
        var (allocated, matches) = MeasureSearch(subject);

        // Assert
        Assert.Equal(0, matches);
        Assert.Equal(0L, allocated);
    }

    private static (long Allocated, int Matches) MeasureSearch(IInterceptorSubject subject)
    {
        // Warm up so JIT compilation and the first read of each parent list do not land inside the
        // measured window.
        for (var i = 0; i < 100; i++)
        {
            subject.TryGetFirstParent<ITraversalMarker>();
        }

        var matches = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredSearches; i++)
        {
            if (subject.TryGetFirstParent<ITraversalMarker>() is not null)
            {
                matches++;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (allocated, matches);
    }
}

[InterceptorSubject]
public sealed partial class ValueEqualParentSubject : IEquatable<ValueEqualParentSubject>
{
    public int Key { get; init; }

    public partial IInterceptorSubject? Child { get; set; }

    public bool Equals(ValueEqualParentSubject? other) => other is not null && Key == other.Key;

    public override bool Equals(object? obj) => obj is ValueEqualParentSubject other && Equals(other);

    public override int GetHashCode() => Key;
}
