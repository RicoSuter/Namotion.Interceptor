using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

public interface ITraversalMarker;

[InterceptorSubject]
public partial class TraversalNode
{
    public partial IInterceptorSubject? Child { get; set; }

    public partial IInterceptorSubject? SecondChild { get; set; }

    public partial IInterceptorSubject? ThirdChild { get; set; }
}

[InterceptorSubject]
public partial class MarkedTraversalNode : ITraversalMarker
{
    public partial IInterceptorSubject? Child { get; set; }

    public partial IInterceptorSubject? SecondChild { get; set; }

    public partial IInterceptorSubject? ThirdChild { get; set; }
}

internal static class ParentTraversalGraph
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    public static IInterceptorSubject CreateNode(bool isMarked)
    {
        return isMarked ? new MarkedTraversalNode() : new TraversalNode();
    }

    /// <summary>
    /// Makes <paramref name="parent"/> a parent of <paramref name="child"/>. Two links between the same
    /// subjects are two parent entries only when their property differs.
    /// </summary>
    public static void Link(IInterceptorSubject child, IInterceptorSubject parent, string propertyName = nameof(TraversalNode.Child))
    {
        child.AddParent(new PropertyReference(parent, propertyName), null);
    }

    /// <summary>
    /// Puts <paramref name="length"/> unmarked subjects above <paramref name="start"/>, one parent each,
    /// and returns the topmost.
    /// </summary>
    public static IInterceptorSubject LinkChain(IInterceptorSubject start, int length)
    {
        var current = start;
        for (var i = 0; i < length; i++)
        {
            var next = new TraversalNode();
            Link(current, next);
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Runs the search off the test thread, so that a search that never ends fails the test instead of
    /// blocking the run.
    /// </summary>
    public static Task<ITraversalMarker?> FindMarkerWithinTimeoutAsync(IInterceptorSubject subject)
    {
        return Task.Run(() => subject.TryGetFirstParent<ITraversalMarker>()).WaitAsync(SearchTimeout);
    }
}
