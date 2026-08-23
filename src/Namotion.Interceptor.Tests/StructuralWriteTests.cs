using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class StructuralWriteTests
{
    /// <summary>
    /// Performs an attachment transition through the raw seam from inside the write chain, while
    /// the executor holds the attachment monitor. The monitor is reentrant on the writing thread,
    /// so this is the one shape that can transition the attachment mid-write.
    /// </summary>
    private sealed class AttachmentTransitionInterceptor : IWriteInterceptor
    {
        public IInterceptorExecutor? Executor;
        public IInterceptorSubjectContext? ContextToAttach;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Assert.True(Executor!.TryUpdateAttachment(Executor.AttachmentRevision, ContextToAttach, SubjectAnchorKind.None, out _));
            next(ref context);
        }
    }

    /// <summary>
    /// Pauses the writing thread inside the chain so the test thread can transition the attachment
    /// while the write is provably between entry and terminal.
    /// </summary>
    private sealed class GatingWriteInterceptor : IWriteInterceptor
    {
        public readonly ManualResetEventSlim ReachedChain = new(false);
        public readonly ManualResetEventSlim ResumeWrite = new(false);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            ReachedChain.Set();
            Assert.True(ResumeWrite.Wait(TimeSpan.FromSeconds(30)));
            next(ref context);
        }
    }

    private sealed class RevisionCapturingInterceptor : IWriteInterceptor
    {
        public readonly List<long> Revisions = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            Revisions.Add(context.Revision);
        }
    }

    private static IInterceptorExecutor GetExecutor(IInterceptorSubject subject)
    {
        return (IInterceptorExecutor)subject.Context;
    }

    [Fact]
    public void WhenAttachmentIsUnchanged_ThenStructuralWriteCommitsLikeSetPropertyValue()
    {
        // Arrange: no write interceptor, so the zero-interceptor structural terminal runs.
        var context = InterceptorSubjectContext.Create();
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        int? structuralValue = null;
        int? scalarValue = null;

        // Act
        var structuralWritten = executor.SetStructuralPropertyValue("Speed", 42, 0, (_, value) => structuralValue = value);
        var scalarWritten = executor.SetPropertyValue("Speed", 43, 42, (_, value) => scalarValue = value);

        // Assert: both routes committed, stamped write state, and consumed one commit revision each
        // from the same per-subject counter.
        Assert.True(structuralWritten);
        Assert.True(scalarWritten);
        Assert.Equal(42, structuralValue);
        Assert.Equal(43, scalarValue);

        var property = new PropertyReference((IInterceptorSubject)subject, "Speed");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(2, commitRevision);
        Assert.Equal(2, ((InterceptorExecutor)executor).Revision);
    }

    [Fact]
    public void WhenChainHasInterceptors_ThenStructuralWriteRunsThemAndStampsTheRevision()
    {
        // Arrange
        var capturing = new RevisionCapturingInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(capturing);
        var subject = new Car(context);
        var executor = GetExecutor(subject);

        // Act
        var written = executor.SetStructuralPropertyValue("Speed", 42, 0, (_, _) => { });

        // Assert
        Assert.True(written);
        Assert.Equal([1L], capturing.Revisions);
    }

    [Fact]
    public void WhenAttachmentChangesBetweenEntryAndTerminal_ThenStructuralWriteStillCommits()
    {
        // Arrange: the interceptor transitions the attachment mid-chain, deterministically on the
        // writing thread itself. The attachment monitor is reentrant on that thread, so this is
        // the one shape that can move the attachment inside the protocol; it must order rather
        // than fail, because the write was validated for the attachment it entered with.
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;
        transitioning.ContextToAttach = InterceptorSubjectContext.Create();
        var backingWritten = false;

        // Act
        var written = executor.SetStructuralPropertyValue("Speed", 42, 0, (_, _) => backingWritten = true);

        // Assert
        Assert.True(written);
        Assert.True(backingWritten);
        var property = new PropertyReference((IInterceptorSubject)subject, "Speed");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
    }

    [Fact]
    public void WhenConcurrentAttachRacesAStructuralWrite_ThenTheAttachWaitsAndBothComplete()
    {
        // Arrange: the writer pauses inside the chain while it holds the attachment monitor, so a
        // concurrent attachment transition must queue on that monitor instead of invalidating the
        // in-flight write. This is the ordering the deleted attachment-revision guard used to turn
        // into an exception.
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        var backingWritten = false;
        Exception? writerException = null;

        var writer = new Thread(() =>
        {
            try
            {
                executor.SetStructuralPropertyValue("Speed", 42, 0, (_, _) => backingWritten = true);
            }
            catch (Exception exception)
            {
                writerException = exception;
            }
        });

        var attachStarting = new ManualResetEventSlim(false);
        var attachCompleted = new ManualResetEventSlim(false);
        var attacher = new Thread(() =>
        {
            var revision = executor.AttachmentRevision;
            attachStarting.Set();
            executor.TryUpdateAttachment(
                revision, InterceptorSubjectContext.Create(), SubjectAnchorKind.None, out _);
            attachCompleted.Set();
        });

        // Act: start the attach while the writer is provably between entry and terminal.
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        attacher.Start();

        // The attach must not complete while the write holds the attachment monitor. Waiting for
        // the attacher to reach the transition first keeps the negative wait from passing
        // vacuously on a thread that never got scheduled.
        Assert.True(attachStarting.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(attachCompleted.Wait(TimeSpan.FromMilliseconds(100)));

        gate.ResumeWrite.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the structural write did not complete");
        Assert.True(attacher.Join(TimeSpan.FromSeconds(30)), "the attachment transition did not complete");

        // Assert: the write committed and the attach landed after it.
        Assert.Null(writerException);
        Assert.True(backingWritten);
        Assert.True(attachCompleted.IsSet);
        Assert.NotNull(executor.AttachedContext);
    }

    [Fact]
    public void WhenAttachmentChangesDuringScalarWrite_ThenTheWriteDoesNotThrow()
    {
        // Arrange: same mid-chain transition as the structural test, but through the scalar route,
        // which must not pay for or react to attachment changes.
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;
        transitioning.ContextToAttach = InterceptorSubjectContext.Create();

        // Act
        subject.Speed = 42;

        // Assert
        Assert.Equal(42, subject.Speed);
    }

    [Fact]
    public void WhenUnattachedSubjectTakesAStructuralWrite_ThenItShortCircuitsLikeAScalarWrite()
    {
        // Arrange: a subject that was never attached to any context. Its structural setter takes
        // the same short circuit the scalar one does, so construction costs exactly what it costs
        // on an unintercepted object: no executor, no lock, no commit revision, no write state.
        var holder = new StructuralHolder();

        // Act
        holder.Child = new StructuralHolder();
        holder.Count = 7;

        // Assert
        Assert.NotNull(holder.Child);
        Assert.Equal(7, holder.Count);
        Assert.Null(holder.TryGetContext());
        Assert.Equal(0, ((InterceptorExecutor)((IInterceptorSubject)holder).Executor).Revision);
        Assert.False(new PropertyReference(holder, nameof(StructuralHolder.Child)).TryGetWriteState(true, out _, out _));
        Assert.False(new PropertyReference(holder, nameof(StructuralHolder.Count)).TryGetWriteState(true, out _, out _));
    }

    [Fact]
    public void WhenComposedSubjectTakesAStructuralWrite_ThenItRunsTheChainUnderTheAttachmentMonitor()
    {
        // Arrange: once a context is composed the write runs the ordinary chain inside the
        // attachment monitor, so it consumes a commit revision and stamps write state.
        var holder = new StructuralHolder(InterceptorSubjectContext.Create());

        // Act
        holder.Child = new StructuralHolder();

        // Assert
        Assert.Equal(1, ((InterceptorExecutor)((IInterceptorSubject)holder).Executor).Revision);
        Assert.True(new PropertyReference(holder, nameof(StructuralHolder.Child)).TryGetWriteState(true, out var revision, out _));
        Assert.Equal(1, revision);
    }
}

[Attributes.InterceptorSubject]
public partial class StructuralHolder
{
    public partial int Count { get; set; }

    public partial StructuralHolder? Child { get; set; }
}
