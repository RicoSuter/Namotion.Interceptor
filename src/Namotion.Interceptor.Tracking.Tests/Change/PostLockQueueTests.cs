using System.Reactive.Concurrency;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenHandlerWritesDuringAttach_ThenStructuralWriteIsAnnouncedFirstAndDerivedLast(bool handlerMakesStructuralWrite)
    {
        // Arrange: the handler writes a property of the already attached root, whose FullName depends on it. A
        // structural write of the handler takes the lifecycle lock again and releases it, which does not drain the
        // queue.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context) { FirstName = "r" };
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                if (handlerMakesStructuralWrite)
                {
                    root.Father = new Person();
                }

                root.LastName = "l";
            }
        }));
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root.Mother = mother;

        // Assert
        string[] handlerWrites = handlerMakesStructuralWrite ? [nameof(Person.Father), nameof(Person.LastName)] : [nameof(Person.LastName)];
        Assert.Equal(
            [nameof(Person.Mother), .. handlerWrites, nameof(Person.FullName), nameof(Person.FullNameWithPrefix)],
            changes.Where(c => c.Property.Subject == root).Select(c => c.Property.Name).ToArray());
        var structural = changes.Single(c => c.Property.Name == nameof(Person.Mother));
        var derived = changes.Single(c => c.Property.Subject == root && c.Property.Name == nameof(Person.FullName));
        Assert.Equal("r l", derived.GetNewValue<string>());
        Assert.Equal(structural.ChangedTimestamp, derived.ChangedTimestamp);
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
    public void WhenObserverOfStructuralWriteWritesAPropertyQueuedUnderTheLock_ThenItsChangeIsDeliveredAfterTheQueuedOne()
    {
        // Arrange: the handler writes FirstName under the lock, which queues its change, and an observer of the
        // structural write writes FirstName again while the structural write is announced, before the drain.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "1";
            }
        }));
        var deliveries = new List<(string Name, string? Value, bool IsLockFree)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject != root)
            {
                return;
            }

            if (change.Property.Name == nameof(Person.Mother))
            {
                root.FirstName = "2";
            }
            else if (change.Property.Name is nameof(Person.FirstName) or nameof(Person.FullName))
            {
                deliveries.Add((change.Property.Name, change.GetNewValue<string?>(), IsLockFreeForOtherThreads(lifecycle, root)));
            }
        });

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(
            [(nameof(Person.FirstName), "1", true), (nameof(Person.FirstName), "2", true), (nameof(Person.FullName), "2", true)],
            deliveries);
        Assert.Equal("2", root.FirstName);
    }

    [Fact]
    public void WhenObserverOfDrainedChangeWritesAPropertyWithAQueuedChange_ThenTheChangesAreDeliveredInWriteOrder()
    {
        // Arrange: the handler writes FirstName and then LastName under the lock. The observer of the drained
        // FirstName change writes LastName while the handler's LastName change is still queued.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "1";
                root.LastName = "x";
            }
        }));
        var deliveries = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject != root)
            {
                return;
            }

            var value = change.GetNewValue<object?>();
            deliveries.Add($"{change.Property.Name}={(value is Person ? "mother" : value)}");
            if (change.Property.Name == nameof(Person.FirstName))
            {
                root.LastName = "3";
            }
        });

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(
            ["Mother=mother", "FirstName=1", "LastName=x", "LastName=3", "FullName=1 3", "FullNameWithPrefix=Mr. 1 3"],
            deliveries);
        Assert.Equal("3", root.LastName);
    }

    [Fact]
    public void WhenObserverOfTheLastQueuedChangeWritesTheSameProperty_ThenItsChangeIsDeliveredAfterTheQueuedOne()
    {
        // Arrange: the handler queues only the FirstName change. The first observer writes FirstName again while
        // that change, the last pending delivery, is delivered; the second observer records the order.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "1";
            }
        }));
        var observable = context.GetPropertyChangeObservable(ImmediateScheduler.Instance);
        observable.Subscribe(change =>
        {
            if (change.Property.Subject == root &&
                change.Property.Name == nameof(Person.FirstName) &&
                change.GetNewValue<string?>() == "1")
            {
                root.FirstName = "2";
            }
        });
        var deliveries = new List<string?>();
        observable.Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.FirstName))
            {
                deliveries.Add(change.GetNewValue<string?>());
            }
        });

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(["1", "2"], deliveries);
        Assert.Equal("2", root.FirstName);
    }

    [Fact]
    public void WhenObserverOfDrainedChangeMakesStructuralWrite_ThenItsWorkDrainsAfterTheOlderQueuedChanges()
    {
        // Arrange: the observer of the drained FirstName change assigns the father while the handler's LastName
        // change is still queued. That structural write is an anchor of its own, and attaching the father writes
        // its name under the lock.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var root = new Person(context);
        var mother = new Person();
        var father = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "1";
                root.LastName = "x";
            }
            else if (change.IsContextAttach && change.Subject == father)
            {
                father.FirstName = "f";
            }
        }));
        var deliveries = new List<(string Name, bool IsLockFree)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            deliveries.Add(($"{(change.Property.Subject == root ? "root" : "father")}.{change.Property.Name}", IsLockFreeForOtherThreads(lifecycle, root)));
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.FirstName))
            {
                root.Father = father;
            }
        });

        // Act
        root.Mother = mother;

        // Assert
        Assert.Equal(
            [
                ("root.Mother", true), ("root.FirstName", true), ("root.LastName", true), ("root.Father", true),
                ("father.FirstName", true), ("root.FullName", true), ("root.FullNameWithPrefix", true)
            ],
            deliveries);
        Assert.Equal("1 x", root.FullName);
        Assert.Equal("f", father.FullName);
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

    [Theory]
    [InlineData("structuralAttach")]
    [InlineData("contextAttach")]
    [InlineData("structuralDetach")]
    [InlineData("tryRunWhileAttached")]
    public async Task WhenLockHolderWritesWhileSynchronousObserverMakesStructuralWrite_ThenBothComplete(string operation)
    {
        // Arrange: the observer holds the dispatch gate of the observable while it waits for the lifecycle lock,
        // which the other thread holds while its handler or callback writes.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var root = new Person(context);
        var source = new Person(context);
        var attached = new Person();
        if (operation == "structuralDetach")
        {
            root.Mother = attached;
        }

        using var observerEntered = new ManualResetEventSlim();
        using var handlerReached = new ManualResetEventSlim();
        var handlerSawObserver = false;
        var observerSawHandler = false;
        Person? written = null;
        void GatedWrite(Person target)
        {
            handlerReached.Set();
            handlerSawObserver = observerEntered.Wait(Timeout);
            target.FirstName = "h";
            written = target;
        }

        var isArmed = 1;
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (GetGatedWriteTarget(operation, change, root, source, attached) is { } target &&
                Interlocked.Exchange(ref isArmed, 0) == 1)
            {
                GatedWrite(target);
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
        var lockHolder = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            RunUnderLifecycleLock(operation, context, lifecycle, root, attached, () => GatedWrite(root)));
        await Task.WhenAll(writer, lockHolder).WaitAsync(Timeout);

        // Assert
        Assert.True(handlerSawObserver);
        Assert.True(observerSawHandler);
        Assert.Equal("h", written?.FirstName);
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

    [Theory]
    [InlineData("attach")]
    [InlineData("detach")]
    [InlineData("tryRunWhileAttached")]
    public void WhenWriteIsMadeUnderTheLockOfAnOperationWithoutStructuralWrite_ThenChangesAreDeliveredAfterTheRelease(string operation)
    {
        // Arrange: a context attach, a context detach or TryRunWhileAttached takes the lifecycle lock without a
        // structural write, so the release of the lock is the anchor.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var existing = new Person(context);
        var leaving = new Person(context);
        var isUnderLock = false;
        void Write()
        {
            isUnderLock = true;
            existing.FirstName = "f";
            isUnderLock = false;
        }

        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            var isOperation = operation == "attach" ? change.IsContextAttach : operation == "detach" && change.IsContextDetach;
            if (isOperation && change.Subject != existing)
            {
                Write();
            }
        }));
        var deliveries = new List<(string Name, bool IsUnderLock, bool IsLockFree)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == existing)
            {
                deliveries.Add((change.Property.Name, isUnderLock, IsLockFreeForOtherThreads(lifecycle, existing)));
            }
        });

        // Act
        switch (operation)
        {
            case "attach":
                _ = new Person(context);
                break;
            case "detach":
                ((IInterceptorSubject)leaving).Context.RemoveFallbackContext(context);
                break;
            default:
                Assert.True(lifecycle.TryRunWhileAttached(existing, Write));
                break;
        }

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
        PropertyAccessObservation.ReplaceWriteObserverForTests(new IgnoringWriteObserver());
        try
        {
            root.Mother = mother;
        }
        finally
        {
            PropertyAccessObservation.ReplaceWriteObserverForTests(trackingObserver);
        }

        // Assert
        Assert.Equal([nameof(Person.FirstName), nameof(Person.Mother)], deliveries);
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

    // The subject the handler writes on the change the operation makes under the lock, null on any other change.
    private static Person? GetGatedWriteTarget(
        string operation, SubjectLifecycleChange change, Person root, Person source, Person attached)
    {
        return operation switch
        {
            "structuralAttach" when change.IsContextAttach && change.Subject == attached => attached,
            "contextAttach" when change is { IsContextAttach: true, Subject: Person person } && person != root && person != source => person,
            "structuralDetach" when change.IsContextDetach && change.Subject == attached => root,
            _ => null
        };
    }

    private static void RunUnderLifecycleLock(
        string operation, IInterceptorSubjectContext context, LifecycleInterceptor lifecycle, Person root, Person attached, Action write)
    {
        switch (operation)
        {
            case "structuralAttach":
                root.Mother = attached;
                break;
            case "contextAttach":
                _ = new Person(context);
                break;
            case "structuralDetach":
                root.Mother = null;
                break;
            default:
                Assert.True(lifecycle.TryRunWhileAttached(root, write));
                break;
        }
    }
}
