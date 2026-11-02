using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Property change observers run only after the lifecycle work a write queued has settled, so an
/// observer sees every subject the write or its callbacks attached registered and every subject they
/// released already detached, including writes made from attach callbacks and seeding getters.
/// </summary>
[Collection(PerPropertySubscriptionCollection.Name)]
public class CallbackPublicationOrderTests
{
    public CallbackPublicationOrderTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenAttachCallbackWritesNewChildBeforePropertyAdmission_ThenObserverRunsAfterRegistration()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(context);
        var child = new Person();
        var grandchild = new Person();
        var events = new List<string>();
        using var subscription = new PropertyReference(grandchild, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.NotNull(grandchild.TryGetRegisteredSubject());
                Assert.Same(context, grandchild.GetContext());
                Assert.Equal("initialized", change.GetNewValue<string>());
                events.Add("scalar-observer");
            });
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                events.Add("child-callback-start");
                child.Father = grandchild;
                grandchild.FirstName = "initialized";
                events.Add("child-callback-end");
            }
            if (ReferenceEquals(change.Subject, grandchild)) events.Add("grandchild-attached");
        };

        // Act
        root.Father = child;

        // Assert
        Assert.Equal(["child-callback-start", "child-callback-end", "grandchild-attached", "scalar-observer"], events);
    }

    [Fact]
    public void WhenAttachCallbackRemovesChild_ThenPropertyObserverRunsAfterDepartureAndDoesNotTrackScalarWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var departing = new Person();
        var root = new Person(context) { Father = departing };
        var arriving = new Person();
        var events = new List<string>();
        using var scalarSubscription = new PropertyReference(departing, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange _) => events.Add("departing-scalar"));
        using var structuralSubscription = new PropertyReference(root, nameof(Person.Father))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.Null(departing.TryGetContext());
                Assert.Null(departing.TryGetRegisteredSubject());
                departing.FirstName = "departed";
                events.Add("structural-observer");
            });
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, arriving))
            {
                events.Add("attach-callback-start");
                root.Father = null;
                events.Add("attach-callback-end");
            }
        };
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, departing))
            {
                Assert.Same(context, departing.GetContext());
                events.Add("departing-detach");
            }
        };

        // Act
        root.Mother = arriving;

        // Assert
        Assert.Equal(["attach-callback-start", "attach-callback-end", "departing-detach", "structural-observer"], events);
        Assert.Equal("departed", departing.FirstName);
    }

    [Fact]
    public void WhenASeedingGetterWritesItsOwnProperty_ThenTheObserverRunsAfterTheChildIsRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new PayloadNode();
        var root = new ReadHookSegmentHolder();
        var events = new List<string>();
        using var subscription = new PropertyReference(root, nameof(ReadHookSegmentHolder.Children))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.Same(child, Assert.Single(change.GetNewValue<ArraySegment<PayloadNode>>()));
                Assert.NotNull(child.TryGetRegisteredSubject());
                Assert.Equal(1, child.GetReferenceCount());
                events.Add("property-observer");
            });
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child)) events.Add("child-attached");
        };
        root.OnRead = () =>
        {
            if (root.TryGetContext() is null) return;
            root.OnRead = null;
            root.Children = new([child]);
            events.Add("setter-returned");
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(["setter-returned", "child-attached", "property-observer"], events);
    }

    [Fact]
    public void WhenObserversQueueMoreLifecycleWork_ThenEachPropertyPublicationWaitsForItAndKeepsWriteOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new PayloadNode();
        var grandchild = new PayloadNode();
        var replacement = new PayloadNode();
        var root = new ReadHookSegmentHolder();
        var events = new List<string>();
        using var rootSubscription = new PropertyReference(root, nameof(ReadHookSegmentHolder.Children))
            .SubscribeInline((in SubjectPropertyChange _) =>
            {
                Assert.NotNull(child.TryGetRegisteredSubject());
                Assert.NotNull(grandchild.TryGetRegisteredSubject());
                events.Add("root-observer");
                child.Child = replacement;
            });
        using var childSubscription = new PropertyReference(child, nameof(PayloadNode.Child))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                Assert.Same(replacement, child.Child);
                Assert.NotNull(replacement.TryGetRegisteredSubject());
                Assert.Null(grandchild.TryGetRegisteredSubject());
                events.Add(ReferenceEquals(change.GetNewValue<PayloadNode>(), grandchild)
                    ? "first-child-write" : "second-child-write");
            });
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                events.Add("child-attached");
                child.Child = grandchild;
            }
            if (ReferenceEquals(change.Subject, grandchild)) events.Add("grandchild-attached");
            if (ReferenceEquals(change.Subject, replacement)) events.Add("replacement-attached");
        };
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, grandchild)) events.Add("grandchild-detached");
        };
        root.OnRead = () =>
        {
            if (root.TryGetContext() is null) return;
            root.OnRead = null;
            root.Children = new([child]);
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(["child-attached", "grandchild-attached", "root-observer", "grandchild-detached", "replacement-attached", "first-child-write", "second-child-write"], events);
    }

    [Fact]
    public void WhenSeedingAndBothPublicationStreamsFail_ThenEveryFailureSurvivesInDeliveryOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new PayloadNode();
        var root = new ReadHookSegmentHolder();
        var seedFailure = new InvalidOperationException("seeding getter");
        var lifecycleFailure = new InvalidOperationException("detach callback");
        var propertyFailure = new InvalidOperationException("property observer");
        using var subscription = new PropertyReference(root, nameof(ReadHookSegmentHolder.Children))
            .SubscribeInline((in SubjectPropertyChange _) => throw propertyFailure);
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, root)) throw lifecycleFailure;
        };
        root.OnRead = () =>
        {
            if (root.TryGetContext() is null) return;
            root.OnRead = null;
            root.Children = new([child]);
            throw seedFailure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Collection(Assert.IsType<AggregateException>(exception).InnerExceptions,
            failure => Assert.Same(seedFailure, failure),
            failure => Assert.Same(lifecycleFailure, failure),
            failure => Assert.Same(propertyFailure, failure));
        Assert.Null(root.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Null(root.TryGetRegisteredSubject());
        Assert.Null(child.TryGetRegisteredSubject());
    }
}
