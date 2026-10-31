using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ScalarReconciliationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenAGetterSupersedesAScalarReplacement_ThenItsHistoryMatchesASingleOccurrenceCollection(
        bool reattachOwner, bool throwAfterNestedWrite)
    {
        // Act
        var scalarHistory = RunReplacement(collection: false, reattachOwner, throwAfterNestedWrite);
        var collectionHistory = RunReplacement(collection: true, reattachOwner, throwAfterNestedWrite);

        // Assert
        Assert.Equal(collectionHistory, scalarHistory);
    }

    /// <summary>
    /// Replaces the root's payload with a trigger whose first read while attached replaces it again with a
    /// final subject, and returns the attach and detach history of that replacement.
    /// </summary>
    private static string[] RunReplacement(bool collection, bool reattachOwner, bool throwAfterNestedWrite)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new PayloadNode();
        root.AttachToContext(context);
        var old = new PayloadNode();
        var final = new PayloadNode();
        var trigger = new ReadHookSegmentHolder();
        var history = new List<string>();
        var identities = new Dictionary<IInterceptorSubject, string>(ReferenceEqualityComparer.Instance)
        {
            [root] = "root",
            [old] = "old",
            [final] = "final"
        };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change => history.Add(identities.GetValueOrDefault(change.Subject, "trigger") + ":attached");
        lifecycle.SubjectDetaching += change => history.Add(identities.GetValueOrDefault(change.Subject, "trigger") + ":detached");
        root.Payload = Wrap(old, collection);
        history.Clear();
        var failure = throwAfterNestedWrite ? new InvalidOperationException("getter failed after replacement") : null;
        trigger.OnRead = CreateSupersedingRead(trigger, root, context, Wrap(final, collection), reattachOwner, failure);

        // Act
        var exception = Record.Exception(() => root.Payload = Wrap(trigger, collection));

        // Assert
        Assert.Same(failure, exception);
        Assert.Null(old.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Equal(1, final.GetReferenceCount());
        var parent = Assert.Single(final.GetParents());
        Assert.Same(root, parent.Property.Subject);
        Assert.Equal(collection ? 0 : (object?)null, parent.Index);
        Assert.NotNull(context.GetService<ISubjectRegistry>().TryGetRegisteredSubject(final));
        root.Payload = null;
        Assert.Null(final.TryGetContext());
        root.DetachFromContext(context);
        Assert.Empty(context.GetService<ISubjectRegistry>().KnownSubjects);
        return history.ToArray();
    }

    private static Action CreateSupersedingRead(
        ReadHookSegmentHolder trigger,
        PayloadNode root,
        IInterceptorSubjectContext context,
        object finalPayload,
        bool reattachOwner,
        Exception? failure)
    {
        return () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            if (reattachOwner) root.DetachFromContext(context);
            root.Payload = finalPayload;
            if (reattachOwner) root.AttachToContext(context);
            if (failure is not null) throw failure;
        };
    }

    private static object Wrap(IInterceptorSubject subject, bool collection) => collection ? new[] { subject } : subject;
}
