using System.Reactive.Concurrency;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A third-party write interceptor with no ordering attributes runs upstream of
/// LifecycleInterceptor (the chain partition compiles every lifecycle last), holding no lock, and
/// can release the writing parent before the lifecycle runs. The write then flows through the
/// lifecycle's write-through arm: no claims and no reconcile, but the terminal's null rule still
/// commits it on the original chain. These tests pin that such a release leaves nothing behind
/// (no subject stays attached through an edge from the released parent, no committed baseline
/// survives for it) and that the commit still delivers change notification, which is exactly the
/// delta an abort-and-retry answer to the released subject would silently drop.
/// </summary>
public class DownstreamWriteInterceptorReleaseTests
{
    [Fact]
    public void WhenADownstreamInterceptorRemovesTheWritingParentsLastSupport_ThenNothingSurvivesTheRelease()
    {
        // Arrange
        var interceptor = new ReleasingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle().WithPropertyChangeSubscriptions();
        context.AddService<IWriteInterceptor>(interceptor);

        var root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        root.Father = parent;

        var child = new Person { FirstName = "C" };
        interceptor.Arm(parent, () => root.Father = null);

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act: the interceptor removes the parent's last support before calling next, so the
        // lifecycle meets an already released parent and takes the write-through arm.
        parent.Father = child;

        // Assert
        Assert.Same(context, root.TryGetContext());
        Assert.Null(parent.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Empty(child.GetParents());
        AssertNoBaseline(context, parent);
        AssertFatherChangeDelivered(changes, parent, child);
    }

    [Fact]
    public void WhenADownstreamInterceptorDetachesTheWritingParentExplicitly_ThenNothingSurvivesTheRelease()
    {
        // Arrange
        var interceptor = new ReleasingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle().WithPropertyChangeSubscriptions();
        context.AddService<IWriteInterceptor>(interceptor);

        var parent = new Person { FirstName = "P" };
        parent.AttachToContext(context);

        var child = new Person { FirstName = "C" };
        interceptor.Arm(parent, () => parent.DetachFromContext(context));

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        parent.Father = child;

        // Assert
        Assert.Null(parent.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Empty(child.GetParents());
        AssertNoBaseline(context, parent);
        AssertFatherChangeDelivered(changes, parent, child);
    }

    /// <summary>
    /// The notification parity pin: the write-through commit must fire the change event on the
    /// chain that carried the write. The final-state asserts above pass even under a wrong null
    /// rule (a retried zero-interceptor commit stores the same value), so this assertion is what
    /// actually distinguishes the write-through arm.
    /// </summary>
    private static void AssertFatherChangeDelivered(List<SubjectPropertyChange> changes, Person parent, Person child)
    {
        Assert.Contains(changes, change =>
            ReferenceEquals(change.Property.Subject, parent) &&
            change.Property.Name == nameof(Person.Father) &&
            ReferenceEquals(change.GetNewValue<Person?>(), child));
    }

    private static void AssertNoBaseline(IInterceptorSubjectContext context, Person parent)
    {
        // The committed baseline is the released parent's outgoing edge record. Baselines are
        // removed exactly once, by the parent's own release, so an entry recreated after that
        // release would survive forever and keep validating edges from a dead owner.
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        Assert.Null(lifecycle.Graph.GetBaseline(new PropertyReference(parent, nameof(Person.Father))));
    }

    /// <summary>
    /// Stands in for a third-party interceptor: no ordering attributes, so registration order
    /// places it downstream of the lifecycle in the resolved write chain.
    /// </summary>
    private sealed class ReleasingWriteInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _target;
        private Action? _release;

        public void Arm(IInterceptorSubject target, Action release)
        {
            _target = target;
            _release = release;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _target))
            {
                // Disarm before acting: the release is itself a structural write that re-enters
                // this interceptor.
                _target = null;
                _release!();
            }

            next(ref context);
        }
    }
}
