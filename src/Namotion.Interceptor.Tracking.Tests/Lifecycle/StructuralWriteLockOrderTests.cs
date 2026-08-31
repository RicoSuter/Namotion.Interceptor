using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Proves the two structural lock orders. Lease admission and protector completion enter the
/// lifecycle gate before one attachment monitor. A structural terminal holds its subject's
/// SyncRoot, then enters the lifecycle gate, then prepares attachment transitions one monitor at
/// a time. Explicit attach captures getters before entering either order.
///
/// Holding an attachment monitor while waiting for the topology gate would deadlock against a
/// publication that holds the gate and needs that monitor. The stress tests drive both directions
/// concurrently. Lifecycle conflicts, and the legacy claim conflict during an exclusive attach,
/// are permitted transient outcomes; any other exception fails. Every join is bounded so an order
/// violation reports progress instead of hanging the suite.
/// </summary>
public class StructuralWriteLockOrderTests
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static void ThrowIfAny(List<Exception> exceptions)
    {
        lock (exceptions)
        {
            if (exceptions.Count > 0)
            {
                throw new AggregateException("a concurrent structural operation threw", exceptions);
            }
        }
    }

    private static void WaitFor(ManualResetEventSlim signal, string phase)
    {
        if (!signal.Wait(WriteProtocolAcceptance.RendezvousTimeout))
        {
            throw new TimeoutException($"Timed out waiting for {phase}.");
        }
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class LeaseAdmissionBarrier(
        IInterceptorSubject target,
        ManualResetEventSlim admitted,
        ManualResetEventSlim resume) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, target) && context.Property.Name == nameof(Person.Mother))
            {
                admitted.Set();
                WaitFor(resume, "the admitted structural write to resume");
            }

            next(ref context);
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenExplicitAttachCapturesAStructuralGetter_ThenSameContextTopologyCanProgress()
    {
        // Arrange
        var context = CreateContext();
        var workerTarget = new Person(context) { FirstName = "worker target" };
        var candidate = new Person { FirstName = "candidate" };
        var getterCalls = 0;
        var workerCompleted = false;
        Exception? workerException = null;
        ((IInterceptorSubject)candidate).AddProperties(new SubjectPropertyMetadata(
            "Captured", typeof(Person), [], _ =>
            {
                getterCalls++;
                if (getterCalls == 1)
                {
                    var worker = new Thread(() =>
                    {
                        workerException = Record.Exception(() => workerTarget.Father = new Person());
                    }) { IsBackground = true };
                    worker.Start();
                    workerCompleted = worker.Join(WriteProtocolAcceptance.RendezvousTimeout);
                }

                return null;
            }, null, isIntercepted: true, isDynamic: true));

        // Act
        candidate.AttachToContext(context);

        // Assert
        Assert.True(workerCompleted,
            "the explicit attach held the topology gate while invoking the structural getter");
        Assert.Null(workerException);
        Assert.Equal(1, getterCalls);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenChildWritesRaceParentRemovals_ThenBothDirectionsCompleteWithoutDeadlock()
    {
        // Arrange: direction one admits through gate then monitor and later completes its terminal
        // through SyncRoot then gate then prepared monitors. Direction two removes the parent edge
        // through the gate and prepares the child's attachment transition through its monitor.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };
        root.Mother = child;

        const int iterations = 3000;
        var exceptions = new List<Exception>();
        var writerProgress = 0;
        var removerProgress = 0;
        var barrier = new Barrier(2);

        var childWriter = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    child.Father = new Person { FirstName = $"F{i}" };
                    child.Father = null;
                }
                catch (LifecycleConflictException)
                {
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.StartsWith("Another context claimed a subject of this graph"))
                {
                }
                catch (Exception exception)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(exception);
                    }
                }

                Volatile.Write(ref writerProgress, i + 1);
            }
        });
        childWriter.IsBackground = true;

        var parentRemover = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    root.Mother = null;
                    root.Mother = child;
                }
                catch (LifecycleConflictException)
                {
                }
                catch (Exception exception)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(exception);
                    }
                }

                Volatile.Write(ref removerProgress, i + 1);
            }
        });
        parentRemover.IsBackground = true;

        // Act
        childWriter.Start();
        parentRemover.Start();
        var childWriterCompleted = childWriter.Join(JoinTimeout);
        var parentRemoverCompleted = childWriterCompleted && parentRemover.Join(JoinTimeout);

        // Assert: completion first, so a lock order defect fails as a timeout instead of hanging.
        Assert.True(childWriterCompleted && parentRemoverCompleted,
            $"probable lock order deadlock: child writer at {Volatile.Read(ref writerProgress)}/{iterations}, " +
            $"parent remover at {Volatile.Read(ref removerProgress)}/{iterations}");
        ThrowIfAny(exceptions);

        // The settled graph is consistent after the permitted transient conflicts above.
        root.Mother = child;
        child.Father = null;
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenStructuralWritesRaceExplicitAttachAndDetach_ThenBothDirectionsCompleteWithoutDeadlock()
    {
        // Arrange: the second direction pair, through the explicit attach and detach entry points
        // rather than a parent edge. A racing write may run on either attachment epoch or report
        // one of the explicitly permitted transient conflicts handled below.
        var context = CreateContext();
        var subject = new Person { FirstName = "S" };

        const int iterations = 3000;
        var exceptions = new List<Exception>();
        var writerProgress = 0;
        var transitionerProgress = 0;
        var barrier = new Barrier(2);

        var writer = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    subject.Father = new Person { FirstName = $"F{i}" };
                    subject.Father = null;
                }
                catch (LifecycleConflictException)
                {
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.StartsWith("Another context claimed a subject of this graph"))
                {
                }
                catch (Exception exception)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(exception);
                    }
                }

                Volatile.Write(ref writerProgress, i + 1);
            }
        });
        writer.IsBackground = true;

        var transitioner = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    subject.AttachToContext(context);
                    subject.DetachFromContext(context);
                }
                catch (LifecycleConflictException)
                {
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.StartsWith("Another context claimed a subject of this graph"))
                {
                }
                catch (Exception exception)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(exception);
                    }
                }

                Volatile.Write(ref transitionerProgress, i + 1);
            }
        });
        transitioner.IsBackground = true;

        // Act
        writer.Start();
        transitioner.Start();
        var writerCompleted = writer.Join(JoinTimeout);
        var transitionerCompleted = writerCompleted && transitioner.Join(JoinTimeout);

        // Assert
        Assert.True(writerCompleted && transitionerCompleted,
            $"probable lock order deadlock: writer at {Volatile.Read(ref writerProgress)}/{iterations}, " +
            $"attach/detach at {Volatile.Read(ref transitionerProgress)}/{iterations}");
        ThrowIfAny(exceptions);
        Assert.Equal(iterations, writerProgress);
        Assert.Equal(iterations, transitionerProgress);

        // The settled graph still tracks writes: the write path did not degrade into a silent
        // no-op under the churn.
        subject.AttachToContext(context);
        var father = new Person { FirstName = "Settled" };
        subject.Father = father;
        Assert.Same(context, subject.TryGetContext());
        Assert.Same(context, father.TryGetContext());
        Assert.Equal(1, father.GetReferenceCount());
    }

    [Fact]
    public void WhenDetachedSubjectTakesAStructuralWrite_ThenTheWriteLandsAndReattachDiscoversIt()
    {
        // Arrange: a subject that was attached and released again keeps its executor, so its
        // structural writes take the unattached fast path: only the attachment monitor, no chain,
        // no lifecycle. The value lands in the backing store and is discovered by the next attach.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };
        root.Mother = child;
        root.Mother = null;
        Assert.Null(child.TryGetContext());

        var father = new Person { FirstName = "F" };

        // Act
        child.Father = father;

        // Assert: the write landed without any lifecycle work.
        Assert.Same(father, child.Father);
        Assert.Null(father.TryGetContext());
        Assert.Equal(0, father.GetReferenceCount());

        // Reattaching seeds the child's structural properties and discovers the father.
        root.Mother = child;
        Assert.Same(context, father.TryGetContext());
        Assert.Equal(1, father.GetReferenceCount());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenProtectorAdmissionPrecedesRemovalPublication_ThenReachabilityIncludesItsClosure()
    {
        // Arrange
        var admitted = new ManualResetEventSlim(false);
        var resume = new ManualResetEventSlim(false);
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context) { FirstName = "root" };
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        root.Father = first;
        first.Father = second;
        second.Father = first;
        context.AddService<IWriteInterceptor>(new LeaseAdmissionBarrier(first, admitted, resume));
        var newChild = new Person { FirstName = "new child" };
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            try
            {
                first.Mother = newChild;
            }
            catch (Exception exception)
            {
                writerException = exception;
            }
        }) { IsBackground = true };

        // Act
        writer.Start();
        WaitFor(admitted, "the structural lease admission");
        try
        {
            root.Father = null;

            // Assert
            Assert.Same(context, first.TryGetContext());
            Assert.Same(context, second.TryGetContext());
        }
        finally
        {
            resume.Set();
        }

        Assert.True(writer.Join(WriteProtocolAcceptance.RendezvousTimeout), "the protected writer never completed");
        Assert.Null(writerException);
        Assert.Null(first.TryGetContext());
        Assert.Null(second.TryGetContext());
        Assert.Null(newChild.TryGetContext());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenProtectorAdmissionRacesADetachingJournal_ThenItUsesTheNewEpochAfterFinalClear()
    {
        // Arrange
        var journalEntered = new ManualResetEventSlim(false);
        var allowJournalToComplete = new ManualResetEventSlim(false);
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var root = new Person(context) { FirstName = "root" };
        var child = new Person { FirstName = "child" };
        var grandchild = new Person { FirstName = "grandchild" };
        root.Father = child;
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, child))
            {
                journalEntered.Set();
                WaitFor(allowJournalToComplete, "the detaching journal to resume");
            }
        };
        Exception? removalException = null;
        var remover = new Thread(() =>
        {
            try
            {
                root.Father = null;
            }
            catch (Exception exception)
            {
                removalException = exception;
            }
        }) { IsBackground = true };

        // Act
        remover.Start();
        WaitFor(journalEntered, "the removal journal");
        var writeException = Record.Exception(() => child.Father = grandchild);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(AttachmentPhase.Detaching,
            ((InterceptorExecutor)((IInterceptorSubject)child).Executor).CurrentAttachmentPhase);
        allowJournalToComplete.Set();

        // Assert
        Assert.True(remover.Join(WriteProtocolAcceptance.RendezvousTimeout), "the removal never completed");
        Assert.Null(removalException);
        Assert.IsType<LifecycleConflictException>(writeException);
        Assert.Null(child.TryGetContext());
        Assert.Null(grandchild.TryGetContext());

        child.Father = grandchild;
        root.Father = child;
        Assert.Same(context, child.TryGetContext());
        Assert.Same(context, grandchild.TryGetContext());
    }
}
