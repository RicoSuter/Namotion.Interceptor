using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceBaseTests
{
    [Fact]
    public async Task WhenStartingSourceAndPushingChanges_ThenUpdatesAreInCorrectOrder()
    {
        // Arrange
        var subjectContextMock = new Mock<IInterceptorSubjectContext>();
        subjectContextMock
            .Setup(s => s.TryGetService<ISubjectRegistry>())
            .Returns(new SubjectRegistry());
        // The ChangeQueueProcessor subscription is now created before StartListeningAsync
        // (capture-early), so the mocked context must provide the change queue upfront.
        subjectContextMock
            .Setup(s => s.TryGetService<PropertyChangeInterceptor>())
            .Returns(new PropertyChangeInterceptor());
        subjectContextMock
            .Setup(context => context.GetServices<SourceMonitor>())
            .Returns(ImmutableArray<SourceMonitor>.Empty);

        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock
            .Setup(s => s.Context)
            .Returns(subjectContextMock.Object);

        var updates = new List<string>();
        var source = new TestSubjectSource(subjectMock.Object, subjectContextMock.Object, NullLogger.Instance)
        {
            StartListeningOverride = (propertyWriter, _) =>
            {
                propertyWriter.Write(updates, u => u.Add("Update1"));
                propertyWriter.Write(updates, u => u.Add("Update2"));
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ =>
                Task.FromResult<Action?>(() => updates.Add("Complete")),
            WriteChangesOverride = (_, _) => ValueTask.FromResult(WriteResult.Success),
        };

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await source.StartAsync(cancellationTokenSource.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => updates.Count >= 3,
            message: "Expected 3 updates (Complete + Update1 + Update2)");
        await source.StopAsync(cancellationTokenSource.Token);

        await cancellationTokenSource.CancelAsync();

        // Assert

        // first apply complete state
        Assert.Equal("Complete", updates.ElementAt(0));

        // then replay since requesting complete state
        Assert.Equal("Update1", updates.ElementAt(1));
        Assert.Equal("Update2", updates.ElementAt(2));
    }

    [Fact]
    public void WhenThePumpCatchesAFailure_ThenItTransitionsBackToSynchronizing()
    {
        // Arrange
        // This was a dynamic test that reached Synchronized and then failed by leaving
        // PropertyChangeInterceptor unconfigured, so constructing the ChangeQueueProcessor threw. The
        // pump now fails fast on that condition before the loop (a source that cannot capture writes is
        // a configuration error, not a retryable fault), so that vector no longer reaches the catch, and
        // nothing between Synchronized being set and ProcessAsync blocking can be made to throw from a
        // mock: the load action runs before the transition, and write failures are absorbed by the retry
        // queue by design.
        //
        // Same not-dynamically-forceable shape as the lock-free State getter pinned above, so the same
        // static-scan technique: pin that the catch still performs the transition, even though the
        // sequence that reaches it is not independently exercised here.
        var source = File.ReadAllText(GetSubjectSourceBaseFilePath());
        var executeAsync = source[source.IndexOf("protected sealed override async Task ExecuteAsync", StringComparison.Ordinal)..];
        var catchBlock = executeAsync[executeAsync.IndexOf("catch (Exception ex)", StringComparison.Ordinal)..];

        // Act & Assert
        Assert.Contains("TransitionStateTo(SourceState.Synchronizing)", catchBlock[..catchBlock.IndexOf("finally", StringComparison.Ordinal)]);
    }

    [Fact]
    public async Task WhenExecuteAsyncExitsViaCancellation_ThenTheFinallyTransitionsToStopped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var source = new TestStateSource(new Person(context));
        var stoppedRaised = new ManualResetEventSlim(false);
        source.StateChanged += (_, sourceEvent) =>
        {
            if (sourceEvent.NewState == SourceState.Stopped)
            {
                stoppedRaised.Set();
            }
        };

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.ExecuteCount >= 1);
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(stoppedRaised.Wait(TimeSpan.FromSeconds(10)), "Expected the finally block to publish Stopped.");
        Assert.Equal(SourceState.Stopped, source.State);
    }

    [Fact]
    public async Task WhenPropertyChangeIsTriggered_ThenWriteToSourceAsyncIsCalled()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        SubjectPropertyChange[]? changes = null;
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (c, _) =>
            {
                changes = c.ToArray();
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await source.StartAsync(cancellationTokenSource.Token);

        subject.FirstName = "Bar";

        await AsyncTestHelpers.WaitUntilAsync(() => changes != null,
            message: "Expected WriteChangesAsync to be called");
        await source.StopAsync(cancellationTokenSource.Token);

        await cancellationTokenSource.CancelAsync();

        // Assert
        Assert.NotNull(changes);
        Assert.Equal("Bar", changes.First().GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenPropertyIsWrittenWhileInitialStateLoads_ThenChangeIsStillWrittenToSource()
    {
        // Arrange: a property write that happens after connection effects become observable
        // in the model but before the change pump subscribes must not be lost. The write is
        // issued inside LoadInitialStateAsync, which runs before the ChangeQueueProcessor
        // was created prior to the capture-early fix, so the change was silently dropped.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);

        var receivedChanges = new ConcurrentQueue<SubjectPropertyChange>();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = _ =>
            {
                subject.FirstName = "written-during-load";
                return Task.FromResult<Action?>(null);
            },
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedChanges.Enqueue(change);
                }
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedChanges.Any(c => c.Property.Name == nameof(Person.FirstName)),
            message: "Expected the write made during initial state load to reach the source");
        await source.StopAsync(CancellationToken.None);

        // Assert
        var received = receivedChanges.First(c => c.Property.Name == nameof(Person.FirstName));
        Assert.Equal("written-during-load", received.GetNewValue<string?>());
    }

    /// <summary>
    /// Rewrites inbound string values, standing in for a hook that transforms what a source sent
    /// (unit normalization, rounding, clamping).
    /// </summary>
    private sealed class UppercasingInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.NewValue is string text)
            {
                context.NewValue = (TProperty)(object)text.ToUpperInvariant();
            }

            next(ref context);
        }
    }

    [Fact]
    public async Task WhenInboundInitialValueIsTransformedByHook_ThenTransformedValueReachesSource()
    {
        // Arrange: the source applies its snapshot during the initial state load and a hook rewrites
        // the value on the way in, so what is stored is not what the source sent. The origin therefore
        // demotes from FromSource to Local, and the transformed value has to reach the source once the
        // load completes: the source still holds the untransformed value, and nothing else reconciles
        // the two. This is the initial-load scenario the typed ChangeOrigin design (#366) delegates to
        // this capture window, so it must keep working if the drain filter is ever changed.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();
        context.AddService<IWriteInterceptor>(new UppercasingInterceptor());

        var subject = new Person(context);

        var receivedChanges = new ConcurrentQueue<SubjectPropertyChange>();
        TestSubjectSource source = null!;
        source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(() =>
                subject.TryGetRegisteredSubject()!
                    .TryGetProperty(nameof(Person.FirstName))!
                    .SetValueFromSource(source, null, null, "server-value")),
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedChanges.Enqueue(change);
                }
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedChanges.Any(c => c.Property.Name == nameof(Person.FirstName)),
            message: "Expected the hook-transformed initial value to reach the source");
        await source.StopAsync(CancellationToken.None);

        // Assert: the source is told the value the model actually holds, not the one it sent.
        Assert.Equal("SERVER-VALUE", subject.FirstName);
        var received = receivedChanges.First(c => c.Property.Name == nameof(Person.FirstName));
        Assert.Equal("SERVER-VALUE", received.GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenTwoWindowWritesToOnePropertyAreSplitByAnInboundApply_ThenTheNewerWriteWins()
    {
        // Arrange: two local writes to the same property during the connect window, with an
        // inbound source apply landing between them so both are captured against the same
        // baseline. Reconciliation walks captured changes in order against the live value, so
        // restoring the older one moves the model and makes the newer one look diverged.
        // Last writer must still win.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context) { FirstName = "A" };

        var receivedValues = new ConcurrentQueue<string?>();
        TestSubjectSource source = null!;
        source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = _ =>
            {
                var property = subject.TryGetRegisteredSubject()!
                    .TryGetProperty(nameof(Person.FirstName))!;

                subject.FirstName = "B";

                // The source reports the value it still holds, which resets the baseline, so the
                // next local write is captured against A rather than against B.
                property.SetValueFromSource(source, null, null, "A");

                subject.FirstName = "C";

                // A second report leaves the model on A at reconcile time, so the older write's
                // old value matches the live value and the newer write's does not.
                property.SetValueFromSource(source, null, null, "A");

                return Task.FromResult<Action?>(null);
            },
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedValues.Enqueue(change.GetNewValue<string?>());
                }
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => !receivedValues.IsEmpty,
            message: "Expected a window write to reach the source");
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal("C", subject.FirstName);
        Assert.DoesNotContain("B", receivedValues);
        Assert.Contains("C", receivedValues);
    }

    [Fact]
    public async Task WhenTwoParkedWritesArriveOutOfCommitOrder_ThenTheHigherRevisionWins()
    {
        // Arrange: changes are enqueued after their commit and outside the subject lock, so under
        // concurrent writers a later commit can be parked first. Collapsing two writes to one
        // property must follow the commit revision, not the order they happened to arrive in.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "A" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.FirstName))
                .SetValueFromSource(s, null, null, "A")); // Server didn't change it

        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "A", "C", revision: 20);
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "A", "B", revision: 10);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal("C", subject.FirstName);
        Assert.Contains(writtenChanges, c => c.GetNewValue<string?>() == "C");
        Assert.DoesNotContain(writtenChanges, c => c.GetNewValue<string?>() == "B");
    }

    [Fact]
    public async Task WhenAQueuedWriteIsOverwrittenByInitialState_ThenTheLocalWriteStillWins()
    {
        // Arrange: a write captured while the source is connecting is overwritten by the initial-state
        // snapshot before the pump flushes. The snapshot came from the source, so it does not supersede
        // the write: nothing has committed locally since. Dropping it would discard a write that already
        // committed, with the model reverting and nothing left to re-deliver it.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);

        var receivedChanges = new ConcurrentQueue<SubjectPropertyChange>();
        var initialStateApplied = false;
        TestSubjectSource source = null!;
        source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = _ =>
            {
                subject.FirstName = "stale-user-write";
                return Task.FromResult<Action?>(() =>
                {
                    subject.TryGetRegisteredSubject()!
                        .TryGetProperty(nameof(Person.FirstName))!
                        .SetValueFromSource(source, null, null, "server-value");
                    initialStateApplied = true;
                });
            },
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedChanges.Enqueue(change);
                }
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(subject, nameof(Person.LastName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => Volatile.Read(ref initialStateApplied),
            message: "Expected initial state to be applied");

        // Sentinel write after the snapshot: once it arrives at the source, any earlier flush
        // containing the FirstName write would already have been delivered.
        subject.LastName = "sentinel";
        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedChanges.Any(c => c.Property.Name == nameof(Person.LastName)),
            message: "Expected the sentinel write to reach the source");
        await source.StopAsync(CancellationToken.None);

        // Assert: the user's write reaches the source rather than being discarded by the load.
        Assert.Contains(receivedChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "stale-user-write");
    }

    [Fact]
    public void WhenAnInboundApplyIsTransformedByAHook_ThenItDoesNotAdvanceTheCommitMarker()
    {
        // Arrange: FinalizeOrigin demotes a stamped origin to Local when the stored value differs from
        // the sent value, which is right for publishing (the local model computed it) but must not make
        // the write count as a local commit. If it did, a property carrying a clamp or normalize hook
        // would let an inbound value supersede a local write that had already committed, so whether a
        // user's write survives would depend on whether that property happens to have a hook.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        context.AddService<IWriteInterceptor>(new UppercasingInterceptor());

        var subject = new Person(context);
        var source = new object();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        subject.FirstName = "userwrite";
        Assert.True(property.TryGetWriteState(out var markerAfterLocalWrite, out _, out _));

        // Act: an inbound apply the hook rewrites, so the origin demotes to Local.
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), "server-value"))
        {
            subject.FirstName = "server-value";
        }

        // Assert
        Assert.Equal("SERVER-VALUE", subject.FirstName);
        Assert.True(property.TryGetWriteState(out var markerAfterInboundApply, out _, out _));
        Assert.Equal(markerAfterLocalWrite, markerAfterInboundApply);
    }

    [Fact]
    public async Task WhenATransactionConfirmationNeedsWriteBackDuringConnect_ThenItIsNotDiscarded()
    {
        // Arrange: a transaction writes to the source itself and then applies locally as a confirmation.
        // That apply is normally skipped as an echo, except when a connector has written the property
        // since, because our write can have landed on the source afterwards and left it holding an older
        // value. The connected processor makes that exception; the connect-window drain did not, so a
        // transaction committing while the source reloaded initial state lost the repair permanently.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var receivedChanges = new ConcurrentQueue<SubjectPropertyChange>();
        TestSubjectSource source = null!;
        source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = _ =>
            {
                // Inside the connect window, where the drain is what decides the change's fate.
                var property = new PropertyReference(subject, nameof(Person.FirstName));
                property.MarkPublished();

                using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(source), "confirmed"))
                {
                    subject.FirstName = "confirmed";
                }

                return Task.FromResult<Action?>(null);
            },
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedChanges.Enqueue(change);
                }
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedChanges.Any(c => c.Property.Name == nameof(Person.FirstName)),
            message: "Expected the transaction confirmation to reach the source rather than being dropped by the connect-window drain");
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Contains(receivedChanges, c => c.GetNewValue<string?>() == "confirmed");
    }

    [Fact]
    public async Task WhenAParkedWriteIsSupersededByALaterLocalWrite_ThenOnlyTheLaterOneIsSent()
    {
        // Arrange: this is the case that must still drop. The second local write supersedes the first,
        // and unlike a value that arrived from the source, its own change IS delivered, so it carries the
        // settled value in the dropped one's place and nothing is lost.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: _ => { });

        // The later local write commits normally; the parked change carries the revision from before it,
        // which is what a write captured earlier and flushed late looks like. Parking both writes instead
        // would let CollapsePerProperty merge them before the reconcile loop, so the survivor would take
        // the send branch and this test would pass with the production drop deleted.
        subject.FirstName = "SecondAttempt";
        Assert.True(new PropertyReference(subject, nameof(Person.FirstName))
            .TryGetWriteState(out var firstNameMarker, out _, out _));
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "FirstAttempt",
            revision: firstNameMarker - 1);

        // A second property gives the reconcile something to send, so the wait below has a signal and the
        // drop is observed rather than merely not-yet-flushed.
        WriteAndPark(source, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal("SecondAttempt", subject.FirstName);
        Assert.DoesNotContain(writtenChanges, c => c.GetNewValue<string?>() == "FirstAttempt");

    }

    [Fact]
    public async Task WhenContextHasNoPropertyChangeInterceptor_ThenSourceFailsFastWithActionableMessage()
    {
        // Arrange: without WithFullPropertyTracking/WithPropertyChangeSubscriptions the source cannot capture
        // any writes. That is a configuration error, so the pump must fail fast at startup with a
        // message naming the missing service and the fix, not run silently inert or throw a cryptic
        // "service not found" from deep in the pump.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance);

        // Act & Assert - StartAsync surfaces the faulted ExecuteAsync task
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.StartAsync(CancellationToken.None));
        Assert.Contains("PropertyChangeInterceptor", exception.Message);
        Assert.Contains("WithFullPropertyTracking", exception.Message);
    }

    [Fact]
    public async Task WhenWriteChangesThrowsException_ThenErrorIsLoggedAndServiceContinues()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (_, _) =>
            {
                tcs.TrySetResult();
                throw new Exception("Connection failed");
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        subject.FirstName = "Test";

        // Wait for the write to be attempted
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - service processed the write (exception was logged, not thrown)
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenWriteChangesThrowsOperationCanceled_ThenServiceStops()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (_, _) =>
            {
                tcs.TrySetResult();
                throw new OperationCanceledException();
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        subject.FirstName = "Test";

        // Wait for the write to be attempted
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - write was attempted (OperationCanceledException propagated up)
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenFlushFails_ThenChangesAreEnqueued()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        // First call fails (simulates queued items failing to flush), second succeeds
        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero) // Disable buffering for immediate writes
        {
            WriteChangesOverride = (changes, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    firstCallTcs.TrySetResult();
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("First call fails")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        // First change - will fail and be queued
        subject.FirstName = "First";

        // Wait for first write to be attempted before triggering second
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second change - will succeed and flush the queued item
        subject.FirstName = "Second";

        await secondCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - both writes were attempted (first failed and was retried)
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WhenWriteChangesInBatchesThrowsOperationCanceled_ThenExceptionPropagates()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (_, _) =>
            {
                tcs.TrySetResult();
                throw new OperationCanceledException();
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        subject.FirstName = "Test";

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - OperationCanceledException was thrown (propagates up)
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenWriteChangesInBatchesThrowsException_ThenChangesAreEnqueued()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero) // Disable buffering for immediate writes
        {
            WriteChangesOverride = (changes, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    firstCallTcs.TrySetResult();
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        // First change fails, second triggers retry
        subject.FirstName = "First";

        // Wait for first write to be attempted before triggering second
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subject.FirstName = "Second";

        await secondCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - changes were enqueued and retried
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WhenWriteReturnsFailureResult_ThenFailedChangesAreEnqueuedAndRetried()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeInterceptor();

        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var allWrittenValues = new ConcurrentBag<string?[]>();
        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero)
        {
            WriteChangesOverride = (changes, _) =>
            {
                var current = Interlocked.Increment(ref callCount);
                allWrittenValues.Add(changes.ToArray().Select(c => c.GetNewValue<string?>()).ToArray());

                if (current == 1)
                {
                    firstCallTcs.TrySetResult();
                    // First write fails - WriteResult.Failure should cause SubjectSourceBase to enqueue
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Transient error")));
                }

                if (current >= 3)
                {
                    thirdCallTcs.TrySetResult();
                }

                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        // First change - will return WriteResult.Failure, should be enqueued for retry
        subject.FirstName = "FailedValue";
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second change - triggers retry queue flush (retrying first), then writes second
        subject.FirstName = "SecondValue";

        // Wait for retry flush + new write (3 total calls)
        await thirdCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert
        // 3 calls expected:
        //   1. "FailedValue" -> Failure (enqueued to retry queue)
        //   2. "FailedValue" -> Success (retry from queue flush)
        //   3. "SecondValue" -> Success (new write)
        Assert.True(callCount >= 3,
            $"Expected at least 3 write calls (initial + retry + new), got {callCount}");

        // Verify the failed value was retried (appears in at least 2 calls)
        var failedValueCallCount = allWrittenValues.Count(batch =>
            batch.Any(v => v == "FailedValue"));
        Assert.True(failedValueCallCount >= 2,
            $"Expected 'FailedValue' in at least 2 write calls (original + retry), appeared in {failedValueCallCount}");
    }

    // Optimistic retry re-apply tests

    [Fact]
    public async Task WhenRetryQueueHasNonConflictingValueChange_ThenChangeIsReappliedAndSentToServer()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.FirstName))
                .SetValueFromSource(s, null, null, "Original")); // Server didn't change it

        // Pre-fill retry queue: client changed "Original" -> "ClientChange"
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "ClientChange");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - change was re-applied locally and sent to server
        Assert.Equal("ClientChange", subject.FirstName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientChange");
    }

    [Fact]
    public async Task WhenTheLoadOverwritesAParkedValueWrite_ThenTheLocalWriteWins()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.FirstName))
                .SetValueFromSource(s, null, null, "ServerChanged")); // Server DID change it

        // The client wrote before the load landed, so the load did not supersede it: nothing has
        // committed locally since, and the load's own write came from the source.
        WriteAndPark(source, subject, nameof(Person.FirstName), "Original", "ClientChange");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - the local write is restored and sent rather than silently discarded
        Assert.Equal("ClientChange", subject.FirstName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientChange");
    }

    [Fact]
    public async Task WhenRetryQueueHasNonConflictingObjectRefChange_ThenChangeIsReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var personA = new Person(context) { FirstName = "A" };
        var personB = new Person(context) { FirstName = "B" };
        var subject = new Person(context) { Father = personA };

        var (source, _, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.Father))
                .SetValueFromSource(s, null, null, personA)); // Server didn't change it

        // Pre-fill retry queue: client changed Father from personA -> personB
        EnqueueRetryChange<Person?>(source, subject, nameof(Person.Father), personA, personB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - re-applied
        Assert.Same(personB, subject.Father);
    }

    [Fact]
    public async Task WhenTheLoadOverwritesAParkedObjectRefWrite_ThenTheLocalWriteWins()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var personA = new Person(context) { FirstName = "A" };
        var personB = new Person(context) { FirstName = "B" };
        var personC = new Person(context) { FirstName = "C" };
        var subject = new Person(context) { Father = personA };

        var (source, _, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.Father))
                .SetValueFromSource(s, null, null, personC)); // Server replaced with C

        // Pre-fill retry queue: client changed Father from personA -> personB
        WriteAndPark<Person?>(source, subject, nameof(Person.Father), personA, personB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - the local write wins over the value the load brought in
        Assert.Same(personB, subject.Father);
    }

    [Fact]
    public async Task WhenRetryQueueHasNonConflictingCollectionChange_ThenChangeIsReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var listA = new List<Person>();
        var listB = new List<Person> { new Person(context) { FirstName = "Child" } };
        var subject = new Person(context) { Children = listA };

        var (source, _, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.Children))
                .SetValueFromSource(s, null, null, listA)); // Server didn't replace it

        // Pre-fill retry queue: client replaced collection listA -> listB
        EnqueueRetryChange(source, subject, nameof(Person.Children), listA, listB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - re-applied (reference equality)
        Assert.Same(listB, subject.Children);
    }

    [Fact]
    public async Task WhenTheLoadOverwritesAParkedCollectionWrite_ThenTheLocalWriteWins()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var listA = new List<Person>();
        var listB = new List<Person> { new Person(context) { FirstName = "ClientChild" } };
        var listC = new List<Person> { new Person(context) { FirstName = "ServerChild" } };
        var subject = new Person(context) { Children = listA };

        var (source, _, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.Children))
                .SetValueFromSource(s, null, null, listC)); // Server replaced collection

        // Pre-fill retry queue: client replaced listA -> listB
        WriteAndPark(source, subject, nameof(Person.Children), listA, listB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - the local write wins over the collection the load brought in
        Assert.Same(listB, subject.Children);
    }

    [Fact]
    public async Task WhenOneParkedWriteIsSupersededAndOneIsNot_ThenEachTakesItsOwnBranch()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "OrigFirst", LastName = "OrigLast" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s =>
                // Moves the model off LastName's parked value, so that one takes the restore branch.
                new PropertyReference(subject, nameof(Person.LastName)).SetValueFromSource(s, null, null, "OrigLast"));

        // Two different outcomes in one reconcile pass: FirstName's parked write is superseded by a
        // later local commit and must drop, LastName's is not and must be restored and sent.
        subject.FirstName = "NewerFirst";
        Assert.True(new PropertyReference(subject, nameof(Person.FirstName))
            .TryGetWriteState(out var firstNameMarker, out _, out _));
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "OrigFirst", "ClientFirst",
            revision: firstNameMarker - 1);

        WriteAndPark(source, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal("ClientLast", subject.LastName);
        Assert.DoesNotContain(writtenChanges, c => c.GetNewValue<string?>() == "ClientFirst");
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.LastName) &&
            c.GetNewValue<string?>() == "ClientLast");
    }

    [Fact]
    public async Task WhenTheLoadOverwritesEveryParkedWrite_ThenAllLocalWritesWin()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "OrigFirst", LastName = "OrigLast" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s =>
            {
                new PropertyReference(subject, nameof(Person.FirstName)).SetValueFromSource(s, null, null, "ServerFirst");
                new PropertyReference(subject, nameof(Person.LastName)).SetValueFromSource(s, null, null, "ServerLast");
            });

        WriteAndPark(source, subject, nameof(Person.FirstName), "OrigFirst", "ClientFirst");
        WriteAndPark(source, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - the load moved both properties, and both local writes are restored and sent
        Assert.Equal("ClientFirst", subject.FirstName);
        Assert.Equal("ClientLast", subject.LastName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientFirst");
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.LastName) &&
            c.GetNewValue<string?>() == "ClientLast");
    }

    [Fact]
    public async Task WhenRetryQueueHasNullValues_ThenNullEqualsNullAndChangeIsReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context); // FirstName starts as null

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { }); // Server didn't set it either - stays null

        // Pre-fill retry queue: client changed null -> "ClientValue"
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), null, "ClientValue");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - null == null -> non-conflicting, re-applied
        Assert.Equal("ClientValue", subject.FirstName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientValue");
    }

    [Fact]
    public async Task WhenTheLoadSetsAValueOverAParkedWriteFromNull_ThenTheLocalWriteWins()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context); // FirstName starts as null

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.FirstName))
                .SetValueFromSource(s, null, null, "ServerValue")); // Server set it

        // Pre-fill retry queue: client changed null -> "ClientValue"
        WriteAndPark<string?>(source, subject, nameof(Person.FirstName), null, "ClientValue");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - the local write wins rather than being discarded as a conflict
        Assert.Equal("ClientValue", subject.FirstName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientValue");
    }

    [Fact]
    public async Task WhenRetryQueueIsEmpty_ThenInitializationSucceedsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => new PropertyReference(subject, nameof(Person.FirstName))
                .SetValueFromSource(s, null, null, "ServerValue"));

        // No retry changes enqueued

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.FirstName == "ServerValue",
            message: "Expected initial state to be applied");
        await source.StopAsync(CancellationToken.None);

        // Assert - Initial state applied, no retry changes sent
        Assert.Equal("ServerValue", subject.FirstName);
        Assert.Empty(writtenChanges);
    }

    [Fact]
    public async Task WhenRetryQueueDisabled_ThenInitializationSucceedsWithoutReapply()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        // writeRetryQueueSize: 0 disables the queue
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            writeRetryQueueSize: 0)
        {
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(() =>
            {
                subject.FirstName = "ServerValue";
            }),
            WriteChangesOverride = (_, _) => new ValueTask<WriteResult>(WriteResult.Success),
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.FirstName == "ServerValue",
            message: "Expected initial state to be applied");
        await source.StopAsync(CancellationToken.None);

        // Assert - service runs normally without retry queue
        Assert.Equal("ServerValue", subject.FirstName);
    }

    [Fact]
    public async Task WhenRetryChangeThrowsDuringReapply_ThenRemainingChangesAreStillProcessed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original", LastName = "Original" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { }); // Server didn't change anything

        // Enqueue a change with a bogus property name - Metadata access will throw
        EnqueueRetryChange(source, subject, "NonExistentProperty", "old", "new");

        // Enqueue a valid change after the broken one
        EnqueueRetryChange(source, subject, nameof(Person.LastName), "Original", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - broken change failed, but LastName was still re-applied
        Assert.Equal("ClientLast", subject.LastName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.LastName) &&
            c.GetNewValue<string?>() == "ClientLast");
    }

    private static (TestSubjectSource source,
        ConcurrentBag<SubjectPropertyChange> writtenChanges, TaskCompletionSource writeTcs)
        CreateSourceWithRetryQueue(Person subject, IInterceptorSubjectContext context,
            Action<TestSubjectSource> initialStateAction)
    {
        var writtenChanges = new ConcurrentBag<SubjectPropertyChange>();
        var writeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        TestSubjectSource? source = null;
        source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(50))
        {
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(() =>
            {
                initialStateAction(source!);
            }),
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    writtenChanges.Add(change);
                }
                writeTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim source ownership for common properties
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(subject, nameof(Person.LastName)).SetSource(source);
        new PropertyReference(subject, nameof(Person.Father)).SetSource(source);
        new PropertyReference(subject, nameof(Person.Mother)).SetSource(source);
        new PropertyReference(subject, nameof(Person.Children)).SetSource(source);

        return (source, writtenChanges, writeTcs);
    }

    /// <summary>
    /// Performs the write the way a caller would, so the property carries a real commit revision, then
    /// parks the resulting change the way the connect-window capture does. Parking a synthetic change at
    /// revision 0 would make the reconcile's supersession check vacuous, because a change that orders
    /// against nothing is never superseded.
    /// </summary>
    private static void WriteAndPark<TValue>(TestSubjectSource source,
        IInterceptorSubject subject, string propertyName, TValue oldValue, TValue newValue)
    {
        var property = new PropertyReference(subject, propertyName);
        property.TryGetWriteState(out var revisionBefore, out _, out _);

        property.Metadata.SetValue?.Invoke(subject, newValue);

        Assert.True(property.TryGetWriteState(out var revision, out _, out _) && revision > revisionBefore,
            "the write did not reach a terminal, so the parked change would carry no revision");

        EnqueueRetryChange(source, subject, propertyName, oldValue, newValue, revision);
    }

    private static void EnqueueRetryChange(TestSubjectSource source,
        IInterceptorSubject subject, string propertyName, string? oldValue, string? newValue)
    {
        EnqueueRetryChange<string?>(source, subject, propertyName, oldValue, newValue);
    }

    private static void EnqueueRetryChange<TValue>(TestSubjectSource source,
        IInterceptorSubject subject, string propertyName, TValue oldValue, TValue newValue,
        long revision = 0)
    {
        var queue = source.WriteRetryQueue!;

        var change = SubjectPropertyChange.Create(
            new PropertyReference(subject, propertyName),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue,
            revision);

        queue.Enqueue(new[] { change });
    }

    [Fact]
    public async Task WhenRetryChangeMatchesCurrentModelValue_ThenItIsSentToSource()
    {
        // Arrange: a retry-queued change whose new value is already the current model value
        // (the write survived the load) must be sent, not dropped. The 2-way re-apply dropped it.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "ClientValue" }; // model already holds the new value

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { }); // load leaves FirstName alone

        // Retry queue: Original -> ClientValue (new value already in the model)
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "ClientValue");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - the change was sent to the source (flush branch), not dropped
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientValue");
        Assert.Equal("ClientValue", subject.FirstName);
    }

    [Fact]
    public async Task WhenStartListeningAsyncFails_ThenRetriesAndEventuallySucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context);

        var callCount = 0;
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            retryTime: TimeSpan.FromMilliseconds(50))
        {
            StartListeningOverride = (_, _) =>
            {
                var current = Interlocked.Increment(ref callCount);
                if (current <= 2)
                {
                    throw new InvalidOperationException($"Simulated failure #{current}");
                }
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ =>
                Task.FromResult<Action?>(() => { subject.FirstName = "Loaded"; }),
            WriteChangesOverride = (_, _) => ValueTask.FromResult(WriteResult.Success),
        };

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.FirstName == "Loaded",
            message: "Expected source to retry and eventually load initial state");
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(callCount >= 3, $"Expected at least 3 calls (2 failures + 1 success), got {callCount}");
        Assert.Equal("Loaded", subject.FirstName);
    }

    [Fact]
    public async Task WhenPropertyIsWrittenWhileNotConnected_ThenChangeReachesSourceOnReconnect()
    {
        // Arrange: the first connection attempt writes a property and then fails. Under the old
        // per-connection subscription that write was lost (no subscription across the retry gap).
        // The source-lifetime subscription must capture it and deliver it on the next attempt.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context);

        var receivedChanges = new ConcurrentQueue<SubjectPropertyChange>();
        var attempts = 0;
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            retryTime: TimeSpan.FromMilliseconds(50))
        {
            StartListeningOverride = (_, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    // Write while connecting, then fail the attempt. Captured, then the pump retries.
                    subject.FirstName = "written-while-not-connected";
                    throw new InvalidOperationException("first attempt fails");
                }
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(null), // load leaves FirstName alone
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedChanges.Enqueue(change);
                }
                return ValueTask.FromResult(WriteResult.Success);
            },
        };
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedChanges.Any(c => c.Property.Name == nameof(Person.FirstName)),
            message: "Expected the write made while not connected to reach the source on reconnect");
        await source.StopAsync(CancellationToken.None);

        // Assert
        var received = receivedChanges.First(c => c.Property.Name == nameof(Person.FirstName));
        Assert.Equal("written-while-not-connected", received.GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenStartListeningOverrideSpawnsTaskAndThrows_ThenSpawnedTaskIsCleanedUpBeforeRethrow()
    {
        // Spec section 11 R2: per-connector StartListeningAsync overrides must own their
        // spawned background tasks (OPC UA session-health, MQTT connection-monitor,
        // WebSocket receive+monitor) and clean them up if the override throws after
        // the task is spawned. This is a guard test for the cleanup-before-rethrow
        // pattern at the abstraction level all three connectors share.

        // Arrange
        var subjectContextMock = new Mock<IInterceptorSubjectContext>();
        subjectContextMock
            .Setup(s => s.TryGetService<ISubjectRegistry>())
            .Returns(new SubjectRegistry());
        // The ChangeQueueProcessor subscription is now created before StartListeningAsync
        // (capture-early), so the mocked context must provide the change queue upfront.
        subjectContextMock
            .Setup(s => s.TryGetService<PropertyChangeInterceptor>())
            .Returns(new PropertyChangeInterceptor());
        subjectContextMock
            .Setup(context => context.GetServices<SourceMonitor>())
            .Returns(ImmutableArray<SourceMonitor>.Empty);

        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock
            .Setup(s => s.Context)
            .Returns(subjectContextMock.Object);

        var spawnCount = 0;
        var spawnedTaskCancelled = false;
        var spawnedTaskCompleted = false;
        var cleanupRan = false;

        var source = new TestSubjectSource(
            subjectMock.Object,
            subjectContextMock.Object,
            NullLogger.Instance,
            retryTime: TimeSpan.FromMilliseconds(50))
        {
            StartListeningOverride = async (_, listenToken) =>
            {
                CancellationTokenSource? spawnCts = null;
                Task? spawnedTask = null;
                try
                {
                    spawnCts = CancellationTokenSource.CreateLinkedTokenSource(listenToken);
                    var cts = spawnCts;
                    spawnedTask = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            spawnedTaskCancelled = true;
                        }
                        finally
                        {
                            spawnedTaskCompleted = true;
                        }
                    });

                    Interlocked.Increment(ref spawnCount);
                    throw new InvalidOperationException("simulated post-spawn failure");
                }
                catch
                {
                    cleanupRan = true;
                    if (spawnCts is not null)
                    {
                        try { await spawnCts.CancelAsync().ConfigureAwait(false); } catch { /* best effort */ }
                    }
                    if (spawnedTask is not null)
                    {
                        try { await spawnedTask.ConfigureAwait(false); } catch { /* expected */ }
                    }
                    if (spawnCts is not null)
                    {
                        try { spawnCts.Dispose(); } catch { /* best effort */ }
                    }
                    throw;
                }
            },
        };

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await source.StartAsync(cancellationTokenSource.Token);
        await AsyncTestHelpers.WaitUntilAsync(
            () => Volatile.Read(ref spawnCount) >= 1 && cleanupRan && spawnedTaskCompleted,
            message: "Expected the override to have spawned a task, thrown, and cleaned up.");
        await source.StopAsync(cancellationTokenSource.Token);
        await cancellationTokenSource.CancelAsync();

        // Assert
        Assert.True(cleanupRan, "Cleanup-on-failure block should run before re-throwing.");
        Assert.True(spawnedTaskCancelled, "Spawned task should observe cancellation from the cleanup helper.");
        Assert.True(spawnedTaskCompleted, "Spawned task should run to completion (cancelled), not be left dangling.");
    }

    [Fact]
    public void WhenSubjectSourceBaseDeclaresState_ThenItReadsLockFreeNotUnderStateLock()
    {
        // Arrange
        // The docs' lock-free getter contract (docs/connectors-monitoring.md: State, LastSynchronizedAt
        // and RootSubject must not acquire any lock held while StateChanged is raised) has no dynamic
        // test: SourceMonitor reads source.State while holding its own _lock, and TransitionTo raises
        // StateChanged while holding _stateLock - a regression that made State take _stateLock too
        // would only deadlock under a genuinely concurrent, cross-thread interleaving between a
        // transitioning thread and a monitor read, not something a unit test can force deterministically.
        // Same not-dynamically-testable shape as the drain fence pinned in SourceSubscriptionTests
        // (WhenTheDrainLoopClearsTheDrainingFlag...); use the same static-scan technique: pin the
        // literal implementation actually used, so a regression back to a locking getter is at least
        // caught here, even though the deadlock it would reintroduce is not independently exercised.
        var sourceFilePath = GetSubjectSourceBaseFilePath();
        var source = File.ReadAllText(sourceFilePath);
        var stateProperty = ExtractExpressionBodiedMember(source, "public SourceState State =>");

        // Act & Assert
        Assert.Contains("Volatile.Read", stateProperty);
        Assert.DoesNotContain("_stateLock", stateProperty);
        Assert.DoesNotContain("lock (", stateProperty);
    }

    private static string ExtractExpressionBodiedMember(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find '{signature}' in the source file.");

        var end = source.IndexOf(';', start);
        Assert.True(end >= 0, $"Expected '{signature}' to end with a ';' (expression-bodied member).");

        return source[start..(end + 1)];
    }

    private static string GetSubjectSourceBaseFilePath([CallerFilePath] string testFilePath = "")
    {
        // CallerFilePath is resolved at this call's compile time, from this test file's own path -
        // resilient to whatever the test runner's current directory happens to be (bin/Debug/...),
        // unlike a path built from Environment.CurrentDirectory or the test assembly's location.
        var testDirectory = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "Namotion.Interceptor.Connectors", "SubjectSourceBase.cs"));
    }

    [Fact]
    public async Task WhenOnePropertyBurstsDuringTheConnectWindow_ThenAnotherPropertysWindowWriteIsNotEvicted()
    {
        // Arrange: the retry queue is a bounded ring buffer that drops its oldest entries, so parking
        // window writes raw lets a burst on one property push every other property's write out before
        // the reconcile ever sees it. The queue is sized well below the burst to make that the only
        // way the LastName write can be lost.
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context) { FirstName = "seed", LastName = "seed" };

        var written = new ConcurrentQueue<string>();
        TestSubjectSource source = null!;
        source = new TestSubjectSource(subject, context, NullLogger.Instance, writeRetryQueueSize: 4)
        {
            // Writing inside the load, rather than from the resume action, is what puts these in the
            // connect window: the drain that parks them runs after the load returns.
            LoadInitialStateOverride = _ =>
            {
                subject.LastName = "OwedLastName";
                for (var i = 0; i < 20; i++)
                {
                    subject.FirstName = "F" + i;
                }

                return Task.FromResult<Action?>(null);
            },
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Enqueue(change.Property.Name + "=" + change.GetNewValue<string?>());
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(subject, nameof(Person.LastName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => written.Contains("FirstName=F19"),
            message: "Expected the burst property's final value to reach the source");
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Contains("LastName=OwedLastName", written);
    }

    [Fact]
    public async Task WhenReconcileSendsAnAlreadyCurrentWrite_ThenALaterConfirmationOnThatPropertyIsWrittenBack()
    {
        // Arrange: the reconcile's "already current" branch flushes the retry queue directly instead of
        // going through the processor, so it has to record the write-out itself. A transaction writes to
        // the source and then applies locally, and that apply arrives as a confirmation which is only
        // sent on when a connector has also written the property, which is exactly this case.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        // Set before the source starts, so the source-lifetime subscription never sees this write and
        // only the reconcile can mark the property.
        var subject = new Person(context) { FirstName = "ClientValue" };

        var written = new ConcurrentQueue<string?>();
        TestSubjectSource source = null!;
        source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Enqueue(change.GetNewValue<string?>());
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        var property = new PropertyReference(subject, nameof(Person.FirstName));
        property.SetSource(source);

        // A parked write whose new value the model already holds: the reconcile sends it rather than
        // restoring or dropping it.
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "ClientValue");

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => written.Contains("ClientValue"),
            message: "Expected the reconcile to send the already-current window write");

        using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(source), "Confirmed"))
        {
            subject.FirstName = "Confirmed";
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => written.Contains("Confirmed"),
            message: "Expected the confirmation to be written back to repair the source");

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenStartAsyncIsCalledTwice_ThenTheSecondStartIsIgnored()
    {
        // Arrange
        // A source registered in DI AND attached to the subject graph is started down both paths.
        // Two pumps then run against one source: the first to exit latches Stopped in its finally,
        // terminally, while the second is still listening and applying live values.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

        var subject = new Person(context);
        var recordingLogger = new RecordingLogger();
        var loads = 0;
        using var loaded = new ManualResetEventSlim(false);
        using var source = new TestSubjectSource(subject, context, recordingLogger, writeRetryQueueSize: 0)
        {
            LoadInitialStateOverride = _ =>
            {
                Interlocked.Increment(ref loads);
                loaded.Set();
                return Task.FromResult<Action?>(null);
            }
        };

        // Act
        await source.StartAsync(CancellationToken.None);
        Assert.True(loaded.Wait(TimeSpan.FromSeconds(10)));
        await source.StartAsync(CancellationToken.None);

        // Assert
        Assert.Contains(recordingLogger.Warnings, message => message.Contains("already started"));
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Synchronized);
        Assert.Equal(1, Volatile.Read(ref loads));
    }
}
