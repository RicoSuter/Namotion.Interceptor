using System.Collections.Immutable;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class StructuralWriteTests
{
    // The one SetPropertyValue entry routes on TProperty, so every structural scenario here
    // writes a subject-typed property; the scalar comparisons write an int one.

    /// <summary>
    /// Performs an attachment transition through the raw seam from inside the write chain,
    /// deterministically on the writing thread itself, so the write is provably routed for an
    /// attachment it no longer has when the terminal runs.
    /// </summary>
    private sealed class AttachmentTransitionInterceptor : IWriteInterceptor
    {
        public IInterceptorExecutor? Executor;
        public bool? ObservedIsWritten;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            // Detach, the one transition legal from an attached state: the subject was attached to
            // resolve this very chain, and a direct swap to another context is rejected by design.
            Assert.True(Executor!.TryUpdateAttachment(Executor.AttachmentRevision, null, SubjectAttachmentAnchorKind.None, out _));
            next(ref context);
            ObservedIsWritten = context.IsWritten;
        }
    }

    /// <summary>
    /// Pauses the writing thread inside the chain so the test thread can act while the write is
    /// provably between entry and terminal, and records per execution whether that execution's
    /// terminal committed, so an aborted attempt is visible as a non-committing chain execution.
    /// </summary>
    private sealed class GatingWriteInterceptor : IWriteInterceptor
    {
        public readonly ManualResetEventSlim ReachedChain = new(false);
        public readonly ManualResetEventSlim ResumeWrite = new(false);
        public int Executions;
        public readonly List<bool> CommitObservations = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref Executions);
            ReachedChain.Set();
            Assert.True(ResumeWrite.Wait(TimeSpan.FromSeconds(30)));
            next(ref context);
            CommitObservations.Add(context.IsWritten);
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

    private sealed class NoOpWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }

    /// <summary>
    /// A probe lifecycle for the late-registration scenarios: counts its chain executions and
    /// records whether each of its next() calls committed, so a test observes both that the
    /// compiled chain contained it and that the commit happened inside its frame. The graph entry
    /// points throw because the registering test never attaches or admits anything through the
    /// probe, and a silent no-op there would hide a scenario defect.
    /// </summary>
    private sealed class CountingLifecycleInterceptor : ILifecycleInterceptor
    {
        public int WritePropertyCount;
        public readonly List<bool> CommitObservations = [];

        public bool TryAddProperties(SubjectPropertyRegistration registration) =>
            throw new NotSupportedException("The probe admits no properties.");

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor) =>
            throw new NotSupportedException("The probe attaches no subjects.");

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context) =>
            throw new NotSupportedException("The probe detaches no subjects.");

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref WritePropertyCount);
            next(ref context);
            CommitObservations.Add(context.IsWritten);
        }
    }

    /// <summary>
    /// Detaches the subject and re-attaches it to the opposite context on every chain execution,
    /// before the terminal can evaluate its commit predicate, so every attempt of the write
    /// aborts and re-routes. Registered on both contexts, this is the deliberate ping-pong shape
    /// the attempt bound exists for.
    /// </summary>
    private sealed class PingPongTransitionInterceptor : IWriteInterceptor
    {
        public IInterceptorExecutor? Executor;
        public IInterceptorSubjectContext? Other;
        public int Transitions;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Assert.True(Executor!.TryUpdateAttachment(Executor.AttachmentRevision, null, SubjectAttachmentAnchorKind.None, out _));
            Assert.True(Executor.TryUpdateAttachment(Executor.AttachmentRevision, Other, SubjectAttachmentAnchorKind.Explicit, out _));
            Transitions++;
            next(ref context);
        }
    }

    /// <summary>
    /// Moves the subject to the other context exactly once, forcing the write through one aborted
    /// attempt whose retry commits through the other context's chain.
    /// </summary>
    private sealed class SingleTransitionInterceptor : IWriteInterceptor
    {
        public IInterceptorExecutor? Executor;
        public IInterceptorSubjectContext? Other;
        private bool _transitioned;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!_transitioned)
            {
                _transitioned = true;
                Assert.True(Executor!.TryUpdateAttachment(Executor.AttachmentRevision, null, SubjectAttachmentAnchorKind.None, out _));
                Assert.True(Executor.TryUpdateAttachment(Executor.AttachmentRevision, Other, SubjectAttachmentAnchorKind.Explicit, out _));
            }

            next(ref context);
        }
    }

    private sealed class OriginCapturingInterceptor : IWriteInterceptor
    {
        public ChangeOriginKind? ObservedOriginKind;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (context.IsWritten)
            {
                ObservedOriginKind = context.Origin.Kind;
            }
        }
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
    public void WhenAttachmentChangesBetweenEntryAndTerminal_ThenStructuralWriteStillCommits()
    {
        // Arrange: the interceptor transitions the attachment mid-chain, deterministically on the
        // writing thread itself. The write was routed and validated for the attachment it entered
        // with, so the terminal's null rule commits it in the same pass rather than failing it or
        // re-routing: the subject is unattached at commit time, and the commit is ordered against
        // any future claim by the attachment monitor.
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;
        var backingWritten = false;

        // Act
        var written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => backingWritten = true);

        // Assert: single-pass commit-through, observed by the transitioning chain's own
        // interceptor rather than only by the final state, so a retry-committed write (which
        // would leave the first chain's IsWritten false) cannot pass.
        Assert.True(written);
        Assert.True(backingWritten);
        Assert.True(transitioning.ObservedIsWritten);
        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
    }

    [Fact]
    public void WhenConcurrentDetachRacesAStructuralWrite_ThenTheDetachCompletesAndTheWriteCommitsUnattached()
    {
        // Arrange: the writer pauses inside its chain. The write path holds no lock across the
        // chain, so the raw detach must complete while the write is parked, and the parked write
        // then commits through the terminal's null rule in the same pass: the writer neither
        // throws nor loses the write, and no retry runs.
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        var backingWritten = false;
        var written = false;
        Exception? writerException = null;

        var writer = new Thread(() =>
        {
            try
            {
                written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => backingWritten = true);
            }
            catch (Exception exception)
            {
                writerException = exception;
            }
        });
        writer.IsBackground = true;

        // Act: detach while the writer is provably between entry and terminal. The transition
        // must succeed immediately; under the old scope it parked on the attachment monitor
        // until the write completed.
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, null, SubjectAttachmentAnchorKind.None, out _));
        Assert.Null(executor.AttachedContext);

        gate.ResumeWrite.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the structural write did not complete");

        // Assert
        Assert.Null(writerException);
        Assert.True(written);
        Assert.True(backingWritten);
        Assert.Equal(1, gate.Executions);
        Assert.Equal([true], gate.CommitObservations);
        Assert.Null(executor.AttachedContext);
    }

    [Fact]
    public void WhenLifecycleIsRegisteredWhileAStructuralWriteIsInFlight_ThenTheWriteReRoutesAndCommitsThroughIt()
    {
        // Arrange: a chain resolved before a lifecycle registration carries no gate section, so
        // committing it after the registration would bypass the gate a concurrent
        // post-registration write holds. The terminal's currency check re-routes exactly that
        // shape. The routing-equals-chain invariant stays pinned in its new form: within any
        // single attempt the probe is in the chain if and only if the commit predicate accepted
        // that attempt's state, so the probe-free attempt does not commit and the probe-carrying
        // retry does.
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        var probe = new CountingLifecycleInterceptor();
        var committed = false;

        var writer = new Thread(() =>
            executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => committed = true));
        writer.IsBackground = true;

        // Act: park the write inside its chain, which pins the attempt's chain to the
        // pre-registration state, then register the probe lifecycle and resume.
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        context.AddService(probe);
        gate.ResumeWrite.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the raced write did not complete");

        // Assert: the first attempt executed its chain without committing (the aborted attempt is
        // visible as a non-committing chain execution), and the retry committed through the probe
        // exactly once.
        Assert.Equal(2, gate.Executions);
        Assert.Equal([false, true], gate.CommitObservations);
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Equal([true], probe.CommitObservations);
        Assert.True(committed);
    }

    [Fact]
    public void WhenAPlainServiceIsRegisteredWhileAStructuralWriteIsInFlight_ThenTheWriteCommitsWithoutReRouting()
    {
        // Arrange: the counterpart scoping pin. A registration that changes the compiled write
        // chain but carries no lock obligation (any service that is not a lifecycle) must leave
        // the in-flight write alone: the already-resolved chain commits and never re-runs, and
        // the late interceptor is seen by the next write.
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        var committed = false;

        var writer = new Thread(() => executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => committed = true));
        writer.IsBackground = true;

        // Act
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        context.AddService(new NoOpWriteInterceptor());
        gate.ResumeWrite.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the write did not complete");

        // Assert
        Assert.True(committed);
        Assert.Equal(1, gate.Executions);
        Assert.Equal([true], gate.CommitObservations);
    }

    [Fact]
    public void WhenTheSubjectMovesToAnotherContextMidChain_ThenTheWriteReRoutesThroughTheNewContextsChain()
    {
        // Arrange: a cross-thread transition to another context between routing and terminal. The
        // old context's chain ran only the aborted, non-committing attempt (its interceptors
        // dispatch nothing), and the retry commits through the new context's chain, whose
        // interceptors observe the committed write.
        var gate = new GatingWriteInterceptor();
        var contextA = InterceptorSubjectContext.Create();
        contextA.AddService(gate);
        var observer = new RevisionCapturingInterceptor();
        var contextB = InterceptorSubjectContext.Create();
        contextB.AddService(observer);

        var subject = new StructuralHolder(contextA);
        var executor = GetExecutor(subject);
        var committed = false;
        var written = false;

        var writer = new Thread(() =>
        {
            written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => committed = true);
        });
        writer.IsBackground = true;

        // Act: move the subject while the write is parked mid-chain; a direct swap is rejected,
        // so the raw transition detaches first.
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, null, SubjectAttachmentAnchorKind.None, out _));
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, contextB, SubjectAttachmentAnchorKind.Explicit, out _));
        gate.ResumeWrite.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the write did not complete");

        // Assert
        Assert.True(written);
        Assert.True(committed);
        Assert.Equal(1, gate.Executions);
        Assert.Equal([false], gate.CommitObservations);
        Assert.Equal([1L], observer.Revisions);
    }

    [Fact]
    public void WhenTheExpectedContextWasClearedAndTheSubjectIsReattached_ThenTheTerminalReRoutesInsteadOfCommitting()
    {
        // Arrange: pins the null rule's boundary at the terminal itself. The lifecycle's
        // write-through arm clears ExpectedAttachedContext after observing a released subject, so
        // a subject re-attached (even to the same context) before the commit must fail the
        // predicate and re-route; committing would land a value the re-attach seeding already
        // read past. This drives the terminal directly with the state that arm produces, because
        // no seam exists between the arm and the terminal to park a thread in.
        var context = InterceptorSubjectContext.Create();
        var subject = new StructuralHolder(context);
        var executor = (InterceptorExecutor)GetExecutor(subject);
        var terminal = WriteInterceptorFactory<StructuralHolder?>.Create(ImmutableArray<IWriteInterceptor>.Empty);
        var backingWritten = false;

        var writeContext = new PropertyWriteContext<StructuralHolder?>(
            executor, new PropertyReference((IInterceptorSubject)subject, "Child"), null, new StructuralHolder());
        writeContext.IsStructuralRoute = true;
        writeContext.ExpectedAttachedContext = null;

        // Act: the subject is attached while the terminal expects it unattached.
        terminal(ref writeContext, (_, _) => backingWritten = true);

        // Assert
        Assert.True(writeContext.AttachmentMoved);
        Assert.False(writeContext.IsWritten);
        Assert.False(backingWritten);
        Assert.Equal(0, executor.Revision);
    }

    [Fact]
    public void WhenAnInterceptorTransitionsTheSubjectOnEveryAttempt_ThenTheWriteThrowsAfterTheAttemptBound()
    {
        // Arrange: the deliberate ping-pong shape. Every attempt's chain moves the subject to the
        // opposite context before the terminal, so every attempt aborts; the bounded loop must
        // answer with the diagnostic instead of the silent unvalidated write-through this shape
        // used to get.
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var interceptorA = new PingPongTransitionInterceptor { Other = contextB };
        var interceptorB = new PingPongTransitionInterceptor { Other = contextA };
        contextA.AddService(interceptorA);
        contextB.AddService(interceptorB);

        var subject = new StructuralHolder(contextA);
        var executor = GetExecutor(subject);
        interceptorA.Executor = executor;
        interceptorB.Executor = executor;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => { }));
        Assert.Contains(nameof(StructuralHolder), exception.Message);
        Assert.Contains("Child", exception.Message);
        Assert.Equal(InterceptorExecutor.MaxWriteRouteAttempts, interceptorA.Transitions + interceptorB.Transitions);
    }

    [Fact]
    public void WhenASourceStampedWriteIsForcedThroughOneAbort_ThenTheCommittedStateKeepsTheSourceOrigin()
    {
        // Arrange: one deterministic abort. The first context's interceptor moves the subject to
        // the second context once, so the first attempt aborts and the retry commits through the
        // other chain. The retry threads the already-consumed origin through instead of consuming
        // the drained thread-static slot again, so the committed state still records the source.
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var mover = new SingleTransitionInterceptor { Other = contextB };
        contextA.AddService(mover);
        var capturing = new OriginCapturingInterceptor();
        contextB.AddService(capturing);

        var subject = new StructuralHolder(contextA);
        var executor = GetExecutor(subject);
        mover.Executor = executor;

        var child = new StructuralHolder();
        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        using var scope = PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), child);

        // Act
        var written = executor.SetPropertyValue("Child", child, null, (_, _) => { });

        // Assert: the committing chain observed the source origin, and the write state counts the
        // commit as a source commit (revision visible with source commits included, invisible
        // without).
        Assert.True(written);
        Assert.Equal(ChangeOriginKind.FromSource, capturing.ObservedOriginKind);
        Assert.True(property.TryGetWriteState(true, out var withSourceCommits, out _));
        Assert.Equal(1, withSourceCommits);
        Assert.True(property.TryGetWriteState(false, out var withoutSourceCommits, out _));
        Assert.Equal(0, withoutSourceCommits);
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
    public void WhenUnattachedStructuralWriteGoesThroughTheExecutor_ThenItConsumesACommitRevisionAndStampsWriteState()
    {
        // Arrange: a subject with a published executor but no context. The write runs the
        // zero-interceptor chain rather than bypassing the terminal, so the commit revision and
        // the write state publish exactly as on the scalar route, and a later attach's seeding
        // can rank against the commit.
        var subject = new StructuralHolder();
        var executor = GetExecutor(subject);
        StructuralHolder? structuralValue = null;

        // Act
        var written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, value) => structuralValue = value);

        // Assert
        Assert.True(written);
        Assert.NotNull(structuralValue);
        Assert.Equal(1, ((InterceptorExecutor)executor).Revision);
        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
    }

    [Fact]
    public void WhenAttachedSubjectTakesAStructuralWrite_ThenItConsumesACommitRevisionAndStampsWriteState()
    {
        // Arrange: once a context is attached the write runs the ordinary chain and its terminal,
        // so it consumes a commit revision and stamps write state.
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
