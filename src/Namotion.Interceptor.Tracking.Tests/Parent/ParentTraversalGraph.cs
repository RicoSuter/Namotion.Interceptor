using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

public interface ITraversalMarker;

/// <summary>
/// A subject that can be made a parent of any number of subjects, each link one occurrence.
/// </summary>
public interface ITraversalParent : IInterceptorSubject
{
    IReadOnlyList<IInterceptorSubject> Children { get; set; }
}

[InterceptorSubject]
public partial class TraversalNode : ITraversalParent
{
    public TraversalNode()
    {
        Children = [];
    }

    public partial IReadOnlyList<IInterceptorSubject> Children { get; set; }
}

[InterceptorSubject]
public partial class MarkedTraversalNode : ITraversalMarker, ITraversalParent
{
    public MarkedTraversalNode()
    {
        Children = [];
    }

    public partial IReadOnlyList<IInterceptorSubject> Children { get; set; }
}

internal static class ParentTraversalGraph
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    // Parents come from the lifecycle, so every linked subject is attached. Explicit anchors keep a
    // cycle attached that no root reaches.
    private static readonly IInterceptorSubjectContext Context = InterceptorSubjectContext
        .Create()
        .WithLifecycle();

    public static IInterceptorSubject CreateNode(bool isMarked)
    {
        return isMarked ? new MarkedTraversalNode() : new TraversalNode();
    }

    /// <summary>
    /// Makes <paramref name="parent"/> a parent of <paramref name="child"/>. Every link is its own parent
    /// entry, so linking the same two subjects twice gives the child two entries for that parent.
    /// </summary>
    public static void Link(IInterceptorSubject child, IInterceptorSubject parent)
    {
        Attach(parent);
        Attach(child);

        var traversalParent = (ITraversalParent)parent;
        traversalParent.Children = [.. traversalParent.Children, child];
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
    /// Detaches the subjects again, so that the shared context does not keep every generated graph.
    /// </summary>
    public static void Release(IEnumerable<IInterceptorSubject> subjects)
    {
        foreach (var subject in subjects)
        {
            if (subject.TryGetContext() is not null)
            {
                subject.DetachFromContext(Context);
            }
        }
    }

    /// <summary>
    /// Runs the search off the test thread, so that a search that never ends fails the test instead of
    /// blocking the run.
    /// </summary>
    public static Task<ITraversalMarker?> FindMarkerWithinTimeoutAsync(IInterceptorSubject subject)
    {
        return Task.Run(() => subject.TryGetFirstParent<ITraversalMarker>()).WaitAsync(SearchTimeout);
    }

    private static void Attach(IInterceptorSubject subject)
    {
        if (subject.TryGetContext() is null)
        {
            subject.AttachToContext(Context);
        }
    }
}
