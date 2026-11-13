using System.Reactive.Concurrency;
using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Work that arises under a lifecycle lock (change deliveries and derived recalculations) runs after the lifecycle
/// operation, outside the lock: after the structural write was announced, or at the release of the lock.
/// </summary>
[Collection(DerivedPropertyTraceCollection.Name)]
public class PostLockQueueTests
{
    private static readonly TimeSpan Timeout = TestTimeouts.Default;

    [Fact]
    public void WhenHandlerWritesDuringAttach_ThenStructuralWriteIsAnnouncedFirstAndDerivedLast()
    {
        // Arrange: the handler writes a property of the already attached root, whose FullName depends on it.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context) { FirstName = "r" };
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.LastName = "l";
            }
        }));
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(
            [nameof(Person.Mother), nameof(Person.LastName), nameof(Person.FullName), nameof(Person.FullNameWithPrefix)],
            changes.Where(c => c.Property.Subject == root).Select(c => c.Property.Name).ToArray());
        var structural = changes.Single(c => c.Property.Name == nameof(Person.Mother));
        var derived = changes.Single(c => c.Property.Subject == root && c.Property.Name == nameof(Person.FullName));
        Assert.Equal("r l", derived.GetNewValue<string>());
        Assert.Equal(structural.ChangedTimestamp, derived.ChangedTimestamp);
    }

    [Fact]
    public void WhenHandlerMakesStructuralWriteDuringAttach_ThenItsReleaseOfTheLockDoesNotDrainTheQueue()
    {
        // Arrange: the handler's structural write takes the lifecycle lock again and releases it, while the
        // structural write that attaches the mother still holds it.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context) { FirstName = "r" };
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.Father = new Person();
                root.LastName = "l";
            }
        }));
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(
            [nameof(Person.Mother), nameof(Person.Father), nameof(Person.LastName), nameof(Person.FullName), nameof(Person.FullNameWithPrefix)],
            changes.Where(c => c.Property.Subject == root).Select(c => c.Property.Name).ToArray());
    }

    [Fact]
    public void WhenHandlerMakesStructuralWriteDuringAttach_ThenThatWriteIsAnnouncedBeforeTheWritesOfItsOwnHandlers()
    {
        // Arrange: attaching the mother assigns the father under the lock, and attaching the father writes its name.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context);
        var mother = new Person();
        var father = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.Father = father;
            }
            else if (change.IsContextAttach && change.Subject == father)
            {
                father.FirstName = "f";
            }
        }));
        var deliveries = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name is nameof(Person.Mother) or nameof(Person.Father) or nameof(Person.FirstName))
            {
                deliveries.Add($"{(change.Property.Subject == root ? "root" : "father")}.{change.Property.Name}");
            }
        });

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(["root.Mother", "root.Father", "father.FirstName"], deliveries);
    }

    [Fact]
    public void WhenStructuralWriteRunsThroughTwoLifecycleInterceptors_ThenAllQueuedWorkDrainsWithTheWrite()
    {
        // Arrange
        static IInterceptorSubjectContext Create() => InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithContextInheritance();
        var parent = Create();
        var child = Create();
        child.AddFallbackContext(parent);
        var root = new Person(child) { FirstName = "r" };
        var mother = new Person();
        child.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.LastName = "l";
            }
        }));
        var changes = new List<SubjectPropertyChange>();
        parent.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(
            [nameof(Person.Mother), nameof(Person.LastName), nameof(Person.FullName), nameof(Person.FullNameWithPrefix)],
            changes.Where(c => c.Property.Subject == root).Select(c => c.Property.Name).ToArray());
        Assert.Equal("r l", changes.Single(c => c.Property.Name == nameof(Person.FullName)).GetNewValue<string>());
    }

    [Fact]
    public async Task WhenHandlerWritesWhileSynchronousObserverMakesStructuralWrite_ThenBothComplete()
    {
        // Arrange: the observer holds the dispatch gate of the observable while it waits for the lifecycle lock,
        // which the attaching thread holds while its handler writes.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context);
        var source = new Person(context);
        var attached = new Person();
        using var observerEntered = new ManualResetEventSlim();
        using var handlerReached = new ManualResetEventSlim();
        var handlerSawObserver = false;
        var observerSawHandler = false;
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == attached)
            {
                handlerReached.Set();
                handlerSawObserver = observerEntered.Wait(Timeout);
                attached.FirstName = "h";
            }
        }));
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == source && change.Property.Name == nameof(Person.FirstName))
            {
                observerEntered.Set();
                observerSawHandler = handlerReached.Wait(Timeout);
                root.Father = new Person();
            }
        });

        // Act
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => source.FirstName = "s");
        var attacher = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Mother = attached);
        await Task.WhenAll(writer, attacher).WaitAsync(Timeout);

        // Assert
        Assert.True(handlerSawObserver);
        Assert.True(observerSawHandler);
        Assert.Equal("h", attached.FirstName);
        Assert.NotNull(root.Father);
    }

    [Fact]
    public void WhenObserverThrowsOnQueuedChange_ThenRemainingEntriesRunAndTheWriteThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "m";
                root.LastName = "n";
            }
        }));
        var delivered = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root)
            {
                delivered.Add(change.Property.Name);
            }
        });
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == nameof(Person.FirstName))
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        var exception = Record.Exception(() => root.Mother = mother);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(
            [nameof(Person.Mother), nameof(Person.FirstName), nameof(Person.LastName), nameof(Person.FullName), nameof(Person.FullNameWithPrefix)],
            delivered);
        Assert.Same(mother, root.Mother);
        Assert.Equal(1, mother.GetReferenceCount());
    }

    [Fact]
    public void WhenHandlerWritesDuringContextAttach_ThenChangesAreDeliveredOutsideTheLifecycleLock()
    {
        // Arrange: the root attaches to the context directly, so the release of the lock is the anchor.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var existing = new Person(context);
        var isInHandler = false;
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject != existing)
            {
                isInHandler = true;
                existing.FirstName = "f";
                isInHandler = false;
            }
        }));
        var deliveries = new List<(string Name, bool IsInHandler, bool IsLockFree)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == existing)
            {
                deliveries.Add((change.Property.Name, isInHandler, IsLockFreeForOtherThreads(lifecycle, existing)));
            }
        });

        // Act
        _ = new Person(context);

        // Assert
        Assert.Equal(
            [(nameof(Person.FirstName), false, true), (nameof(Person.FullName), false, true), (nameof(Person.FullNameWithPrefix), false, true)],
            deliveries);
    }

    [Fact]
    public void WhenObserverThrowsOnQueuedChangeOfContextAttach_ThenTheAttachCompletesAndTheExceptionIsTraced()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var existing = new Person(context);
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject != existing)
            {
                existing.FirstName = "f";
            }
        }));
        var delivered = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change => delivered.Add(change.Property.Name));
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == nameof(Person.FirstName))
            {
                throw new InvalidOperationException("observer failure");
            }
        });
        using var traceCapture = new TraceCapture();

        // Act
        var exception = Record.Exception(() => new Person(context));

        // Assert
        Assert.Null(exception);
        Assert.Contains(nameof(Person.FullName), delivered);
        Assert.Contains("observer failure", traceCapture.Text);
    }

    [Fact]
    public void WhenHandlerWritesInsideTryRunWhileAttached_ThenChangeIsDeliveredAfterTheCallback()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var person = new Person(context) { FirstName = "a" };
        var isInCallback = false;
        var deliveries = new List<(string Name, bool IsInCallback)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => deliveries.Add((change.Property.Name, isInCallback)));

        // Act
        var hasRun = lifecycle.TryRunWhileAttached(person, () =>
        {
            isInCallback = true;
            person.FirstName = "b";
            isInCallback = false;
        });

        // Assert
        Assert.True(hasRun);
        Assert.Equal(
            [(nameof(Person.FirstName), false), (nameof(Person.FullName), false), (nameof(Person.FullNameWithPrefix), false)],
            deliveries);
    }

    [Fact]
    public void WhenCallbackOfTryRunWhileAttachedWritesWithItsOwnTimestamp_ThenTheDerivedChangeKeepsThatTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var person = new Person(context) { FirstName = "a" };
        var timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        lifecycle.TryRunWhileAttached(person, () =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(timestamp))
            {
                person.FirstName = "b";
            }
        });

        // Assert
        Assert.Equal(timestamp, changes.Single(c => c.Property.Name == nameof(Person.FullName)).ChangedTimestamp);
    }

    [Fact]
    public void WhenHandlerWritesDuringContextDetach_ThenChangesAreDeliveredOutsideTheLifecycleLock()
    {
        // Arrange: the leaving subject detaches from the context directly, so the release of the lock is the anchor.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var existing = new Person(context);
        var leaving = new Person(context);
        var isInHandler = false;
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextDetach && change.Subject == leaving)
            {
                isInHandler = true;
                existing.FirstName = "f";
                isInHandler = false;
            }
        }));
        var deliveries = new List<(string Name, bool IsInHandler, bool IsLockFree)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == existing)
            {
                deliveries.Add((change.Property.Name, isInHandler, IsLockFreeForOtherThreads(lifecycle, existing)));
            }
        });

        // Act
        ((IInterceptorSubject)leaving).Context.RemoveFallbackContext(context);

        // Assert
        Assert.Equal(
            [(nameof(Person.FirstName), false, true), (nameof(Person.FullName), false, true), (nameof(Person.FullNameWithPrefix), false, true)],
            deliveries);
    }

    [Fact]
    public void WhenDerivedPropertyIsRecalculatedUnderTheLifecycleLock_ThenItsChangeIsDeliveredAfterTheReleaseWithTheTimestampOfTheCall()
    {
        // Arrange: the getter reads state outside the model, so only the manual recalculation publishes it. The call's
        // timestamp is newer than the one the release resolves, so the change keeps it.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var external = 1;
        var worker = new EvaluatorWorker { Evaluator = _ => external };
        var root = new EvaluatorRoot(context) { Machine = new EvaluatorMachine { Worker = worker } };
        var callTimestamp = DateTimeOffset.UtcNow.AddDays(1);
        var isInCallback = false;
        var deliveries = new List<(int NewValue, bool IsInCallback, DateTimeOffset Timestamp)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == worker && change.Property.Name == nameof(EvaluatorWorker.Result))
            {
                deliveries.Add((change.GetNewValue<int>(), isInCallback, change.ChangedTimestamp));
            }
        });

        // Act
        var hasRun = lifecycle.TryRunWhileAttached(worker, () =>
        {
            isInCallback = true;
            external = 2;
            using (SubjectChangeContext.WithChangedTimestamp(callTimestamp))
            {
                new PropertyReference(worker, nameof(EvaluatorWorker.Result)).RecalculateDerivedProperty();
            }

            isInCallback = false;
        });

        // Assert
        Assert.True(hasRun);
        Assert.Same(worker, root.Machine.Worker);
        Assert.Equal([(2, false, callTimestamp)], deliveries);
    }

    [Fact]
    public void WhenAnotherWriteObserverIsInstalled_ThenTheReleaseOfTheLockDeliversTheQueuedChanges()
    {
        // Arrange: the write observer of Tracking completes an anchor write, so with another observer installed in
        // its place the release of the lifecycle lock runs the queued work instead.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "m";
            }
        }));
        var deliveries = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root)
            {
                deliveries.Add(change.Property.Name);
            }
        });
        var trackingObserver = PropertyAccessObservation.WriteObserver;

        // Act
        ReplaceWriteObserver(new IgnoringWriteObserver());
        try
        {
            root.Mother = mother;
        }
        finally
        {
            ReplaceWriteObserver(trackingObserver);
        }

        // Assert
        Assert.Equal([nameof(Person.FirstName), nameof(Person.Mother)], deliveries);
    }

    // The process-wide observer is replaced through the test hook of the core library, which is internal to it.
    private static void ReplaceWriteObserver(IPropertyWriteObserver? observer)
    {
        typeof(PropertyAccessObservation)
            .GetMethod("ReplaceWriteObserverForTests", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [observer]);
    }

    private sealed class IgnoringWriteObserver : IPropertyWriteObserver
    {
        public void OnPropertyWritten(in PropertyWriteObservation write)
        {
            // Stands for a write observer installed by other code; it does not complete anchor writes.
        }
    }

    // True when another thread takes the lifecycle lock, which it does only if this thread holds none.
    private static bool IsLockFreeForOtherThreads(LifecycleInterceptor lifecycle, IInterceptorSubject subject)
    {
        return DedicatedThreadTestHelpers
            .RunOnDedicatedThreadAsync(() => lifecycle.TryRunWhileAttached(subject, () => { }))
            .Wait(Timeout);
    }
}
