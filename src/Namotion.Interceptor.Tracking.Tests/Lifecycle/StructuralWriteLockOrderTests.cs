using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Proves the structural write lock order: lifecycle gate, then attachment monitor, then SyncRoot.
///
/// The inverted order deadlocks: a structural write on a child that entered the child's attachment
/// monitor before the topology gate waits for the gate, while a parent removal that holds the gate
/// reaches the child's release and waits for that same monitor. The stress tests below drive both
/// directions concurrently; a wrong order shows up as a hang, so every join is bounded and fails
/// the test rather than hanging the suite, and the failure message carries each thread's progress
/// so a timeout distinguishes a deadlock from slow progress.
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
    public void WhenChildWritesRaceParentRemovals_ThenBothDirectionsCompleteWithoutDeadlock()
    {
        // Arrange: direction one is a structural write on the child (gate, then the child's
        // attachment monitor); direction two is a parent removal whose release descent hands the
        // child's claim back through that same monitor while holding the gate.
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

        // The settled graph is consistent: transient races ordered instead of throwing.
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
        // rather than a parent edge. The writes on the transitioning subject either run attached
        // (through the gate) or unattached (through the monitor alone); neither outcome throws.
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
    public void WhenProtectorAdmissionFollowsRemovalPublication_ThenItUsesTheNewAttachmentEpoch()
    {
        // Arrange
        var published = new ManualResetEventSlim(false);
        var detachedWriteCompleted = new ManualResetEventSlim(false);
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
                published.Set();
                WaitFor(detachedWriteCompleted, "the detached-epoch write");
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
        WaitFor(published, "the removal publication");
        var writeException = Record.Exception(() => child.Father = grandchild);
        detachedWriteCompleted.Set();

        // Assert
        Assert.True(remover.Join(WriteProtocolAcceptance.RendezvousTimeout), "the removal never completed");
        Assert.Null(removalException);
        Assert.Null(writeException);
        Assert.Null(child.TryGetContext());
        Assert.Null(grandchild.TryGetContext());

        root.Father = child;
        Assert.Same(context, child.TryGetContext());
        Assert.Same(context, grandchild.TryGetContext());
    }
}
