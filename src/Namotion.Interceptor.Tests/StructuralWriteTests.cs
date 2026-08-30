using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests;

public class StructuralWriteTests
{
    // The one SetPropertyValue entry routes on TProperty, so every structural scenario here
    // writes a subject-typed property; the scalar comparisons write an int one.

    private sealed class AttachmentTransitionInterceptor : IWriteInterceptor
    {
        public IInterceptorExecutor? Executor;

        public bool ConflictObserved;

        public bool TransitionSucceeded;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            try
            {
                TransitionSucceeded = Executor!.TryUpdateAttachment(
                    Executor.AttachmentRevision,
                    null,
                    SubjectAttachmentAnchorKind.None,
                    out _);
            }
            catch (LifecycleConflictException)
            {
                ConflictObserved = true;
            }

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

    private sealed class FailedTerminalCapturingInterceptor : IWriteInterceptor
    {
        public bool? IsTerminalCommitted;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            try
            {
                next(ref context);
            }
            catch
            {
                IsTerminalCommitted = context.IsTerminalCommitted;
                throw;
            }
        }
    }

    private sealed class RawWriteException : Exception
    {
    }

    private static IInterceptorExecutor GetExecutor(IInterceptorSubject subject)
    {
        return subject.Executor;
    }

    [Fact]
    public void WhenAttachmentIsUnchanged_ThenStructuralWriteCommitsLikeAScalarWrite()
    {
        // Arrange: no write interceptor, so the zero-interceptor structural terminal runs. The
        // one SetPropertyValue entry routes on TProperty: the subject-typed write takes the
        // structural protocol, the int one the scalar route.
        var context = InterceptorSubjectContext.Create();
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        StructuralHolder? structuralValue = null;
        int? scalarValue = null;
        var child = new StructuralHolder();

        // Act
        var structuralWritten = executor.SetPropertyValue("Child", child, null, (_, value) => structuralValue = value);
        var scalarWritten = executor.SetPropertyValue("Count", 43, 0, (_, value) => scalarValue = value);

        // Assert: both routes committed, stamped write state, and consumed one commit revision each
        // from the same per-subject counter.
        Assert.True(structuralWritten);
        Assert.True(scalarWritten);
        Assert.Same(child, structuralValue);
        Assert.Equal(43, scalarValue);

        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
        Assert.Equal(2, ((InterceptorExecutor)executor).Revision);
    }

    [Fact]
    public void WhenChainHasInterceptors_ThenStructuralWriteRunsThemAndStampsTheRevision()
    {
        // Arrange
        var capturing = new RevisionCapturingInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(capturing);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);

        // Act
        var written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => { });

        // Assert
        Assert.True(written);
        Assert.Equal([1L], capturing.Revisions);
    }

    [Fact]
    public void WhenGeneratedRawStructuralWriterThrows_ThenNoCommitIsReported()
    {
        // Arrange
        var capturing = new FailedTerminalCapturingInterceptor();
        var context = InterceptorSubjectContext.Create().WithService(() => capturing);
        var subject = new StructuralHolder(context);
        var executor = (InterceptorExecutor)GetExecutor(subject);
        StructuralHolder? storedValue = null;
        var property = new PropertyReference(subject, nameof(StructuralHolder.Child));

        // Act & Assert
        Assert.Throws<RawWriteException>(() => executor.SetGeneratedPropertyValue(
            nameof(StructuralHolder.Child),
            new StructuralHolder(),
            _ => storedValue,
            (_, _) => throw new RawWriteException()));
        Assert.False(capturing.IsTerminalCommitted);
        Assert.Null(storedValue);
        Assert.Equal(0, executor.Revision);
        Assert.False(property.TryGetWriteState(true, out _, out _));
    }

    [Fact]
    public void WhenAttachmentTransitionIsAttemptedInsideAStructuralChain_ThenItConflictsAndTheWriteCommits()
    {
        // Arrange
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;
        var backingWritten = false;

        // Act
        var written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => backingWritten = true);

        // Assert
        Assert.True(written);
        Assert.True(backingWritten);
        Assert.True(transitioning.ConflictObserved);
        Assert.False(transitioning.TransitionSucceeded);
        Assert.Same(context, executor.AttachedContext);
        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
    }

    [Fact]
    public async Task WhenConcurrentDetachRacesAStructuralWrite_ThenItFailsPromptlyAndCanBeRetriedAfterTheWrite()
    {
        // Arrange
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        var backingWritten = false;
        Exception? writerException = null;

        var writer = new Thread(() =>
        {
            try
            {
                executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => backingWritten = true);
            }
            catch (Exception exception)
            {
                writerException = exception;
            }
        }) { IsBackground = true };

        var detachCompleted = new ManualResetEventSlim(false);
        Exception? detachException = null;
        var detacher = new Thread(() =>
        {
            try
            {
                var revision = executor.AttachmentRevision;
                executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _);
            }
            catch (Exception exception)
            {
                detachException = exception;
            }
            finally
            {
                detachCompleted.Set();
            }
        }) { IsBackground = true };

        // Act: start the detach while the writer is provably between entry and terminal.
        writer.Start();
        await AsyncTestHelpers.WaitUntilAsync(() => gate.ReachedChain.IsSet);
        detacher.Start();

        await AsyncTestHelpers.WaitUntilAsync(() => detachCompleted.IsSet);
        Assert.IsType<LifecycleConflictException>(detachException);
        Assert.Same(context, executor.AttachedContext);

        gate.ResumeWrite.Set();
        await AsyncTestHelpers.WaitUntilAsync(
            () => !writer.IsAlive && !detacher.IsAlive,
            message: "the structural write and attachment transition did not complete");

        Assert.True(executor.TryUpdateAttachment(
            executor.AttachmentRevision,
            null,
            SubjectAttachmentAnchorKind.None,
            out _));

        // Assert
        Assert.Null(writerException);
        Assert.True(backingWritten);
        Assert.True(detachCompleted.IsSet);
        Assert.Null(executor.AttachedContext);
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

        // Act
        subject.Speed = 42;

        // Assert
        Assert.Equal(42, subject.Speed);
        Assert.True(transitioning.TransitionSucceeded);
        Assert.False(transitioning.ConflictObserved);
    }

    [Fact]
    public void WhenUnattachedSubjectTakesAStructuralWrite_ThenItUsesTheGeneratedTerminal()
    {
        // Arrange
        var holder = new StructuralHolder();

        // Act
        holder.Child = new StructuralHolder();

        // Assert
        Assert.NotNull(holder.Child);
        Assert.Null(holder.TryGetContext());
        Assert.Equal(1, ((InterceptorExecutor)((IInterceptorSubject)holder).Executor).Revision);
        Assert.True(new PropertyReference(holder, nameof(StructuralHolder.Child))
            .TryGetWriteState(true, out var revision, out _));
        Assert.Equal(1, revision);
    }

    [Fact]
    public void WhenAttachedSubjectTakesAStructuralWrite_ThenItRunsTheChainUnderItsLease()
    {
        // Arrange
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
