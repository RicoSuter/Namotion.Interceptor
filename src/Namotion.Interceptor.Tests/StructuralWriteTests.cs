using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class StructuralWriteTests
{
    /// <summary>
    /// Performs an attachment transition through the raw seam from inside the write chain, after the
    /// structural entry captured its attachment revision but before the terminal re-checks it.
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
    public void WhenAttachmentChangesBetweenEntryAndTerminal_ThenStructuralWriteThrowsBeforeTheBackingWrite()
    {
        // Arrange: the interceptor transitions the attachment mid-chain, deterministically on the
        // writing thread itself.
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;
        transitioning.ContextToAttach = InterceptorSubjectContext.Create();
        var backingWritten = false;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => executor.SetStructuralPropertyValue("Speed", 42, 0, (_, _) => backingWritten = true));
        Assert.False(backingWritten);

        // The terminal aborted before committing anything: no commit revision was consumed and no
        // write state was stamped.
        Assert.Equal(0, ((InterceptorExecutor)executor).Revision);
        var property = new PropertyReference((IInterceptorSubject)subject, "Speed");
        Assert.False(property.TryGetWriteState(true, out _, out _));
    }

    [Fact]
    public void WhenConcurrentAttachLandsWhileStructuralWriteIsInFlight_ThenTheWriteThrowsBeforeTheBackingWrite()
    {
        // Arrange
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        var backingWritten = false;
        Exception? thrown = null;

        var writer = new Thread(() =>
        {
            try
            {
                executor.SetStructuralPropertyValue("Speed", 42, 0, (_, _) => backingWritten = true);
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
        });

        // Act: transition the attachment while the writer is paused between entry and terminal.
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(executor.TryUpdateAttachment(
            executor.AttachmentRevision, InterceptorSubjectContext.Create(), SubjectAnchorKind.None, out _));
        gate.ResumeWrite.Set();
        writer.Join();

        // Assert
        Assert.IsType<InvalidOperationException>(thrown);
        Assert.False(backingWritten);
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
    public void WhenAttachedSubjectTakesAStructuralWrite_ThenItRunsThroughTheGuardedTerminal()
    {
        // Arrange: the same write once a context exists takes the guarded route, which is what the
        // attachment revision protects.
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
