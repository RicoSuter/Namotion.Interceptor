using System.Diagnostics;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Change.DerivedPropertyTestHelpers;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(DerivedPropertyTraceCollection.Name)]
public class DerivedPropertyOwnershipTests
{
    private static readonly TimeSpan Timeout = TestTimeouts.Default;

    [Fact]
    public async Task WhenGetterTakesLockWhileItsDependencyIsWrittenDuringAttach_ThenBothComplete()
    {
        // Arrange: Anchor is written while the first attach evaluation runs, so the version check makes the
        // attach evaluate again after it linked Anchor. Once that second evaluation started, a writer takes the
        // lock the evaluation waits for and writes Anchor, which requests the recalculation. The attach evaluates
        // without holding lock(data), so the request does not wait for the attach, and the attach does not wait
        // for the writer under lock(data).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var sync = new object();
        using var firstPassRead = new ManualResetEventSlim();
        using var firstWriteDone = new ManualResetEventSlim();
        using var secondEvaluationStarted = new ManualResetEventSlim();
        using var writerHoldsLock = new ManualResetEventSlim();
        var evaluations = 0;
        var attachThread = 0;
        var secondEvaluationThread = 0;
        machine.Worker.Evaluator = _ =>
        {
            var evaluation = Interlocked.Increment(ref evaluations);
            if (evaluation == 1)
            {
                var anchor = root.Anchor;
                firstPassRead.Set();
                firstWriteDone.Wait(Timeout);
                return anchor;
            }

            if (evaluation == 2)
            {
                secondEvaluationThread = Environment.CurrentManagedThreadId;
                secondEvaluationStarted.Set();
                writerHoldsLock.Wait(Timeout);
            }

            lock (sync)
            {
                return root.Anchor;
            }
        };

        // Act
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            attachThread = Environment.CurrentManagedThreadId;
            root.Machine = machine;
        });
        Assert.True(firstPassRead.Wait(Timeout));
        root.Anchor = 1;
        firstWriteDone.Set();
        Assert.True(secondEvaluationStarted.Wait(Timeout));
        var write = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            lock (sync)
            {
                writerHoldsLock.Set();
                root.Anchor = 5;
            }
        });

        // Assert
        await Task.WhenAll(attach, write).WaitAsync(Timeout);
        Assert.Equal(attachThread, secondEvaluationThread);
        Assert.Equal(5, machine.Worker.Result);
    }

    [Fact]
    public void WhenGetterThrowsAtAttach_ThenFirstSuccessIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor == 0 ? throw new InvalidOperationException() : 10;
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        root.Anchor = 1;

        // Assert
        var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker && c.Property.Name == nameof(EvaluatorWorker.Result));
        Assert.Equal(0, change.GetOldValue<int>());
        Assert.Equal(10, change.GetNewValue<int>());
    }

    [Fact]
    public void WhenSubjectIsAttachedToSecondGraph_ThenItsDerivedPropertyIsNotEvaluatedAgain()
    {
        // Arrange: the getter reads state outside the model, so only an evaluation sees it change.
        var context1 = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var context2 = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root1 = new EvaluatorRoot(context1);
        var holder2 = new EvaluatorMachine(context2);
        var machine = new EvaluatorMachine();
        var worker = machine.Worker;
        var external = 1;
        worker.Evaluator = _ => Volatile.Read(ref external);
        root1.Machine = machine;
        var result = new PropertyReference(worker, nameof(EvaluatorWorker.Result));
        var changes = SubscribeToChanges(context1);
        Volatile.Write(ref external, 2);

        // Act
        holder2.Worker = worker;
        result.RecalculateDerivedProperty();

        // Assert: the second attach kept the published value, so the recalculation publishes the change from it.
        var change = Assert.Single(changes, c => c.Property == result);
        Assert.Equal(1, change.GetOldValue<int>());
        Assert.Equal(2, change.GetNewValue<int>());
    }

    [Fact]
    public void WhenSubjectIsDetachedFromOneOfTwoGraphs_ThenDerivedPropertyStillPublishes()
    {
        // Arrange
        var context1 = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var context2 = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root1 = new Person(context1);
        var root2 = new Person(context2);
        var person = new Person { FirstName = "a" };
        root1.Mother = person;
        root2.Mother = person;
        var changes = SubscribeToChanges(context1);

        // Act
        root2.Mother = null;
        person.FirstName = "b";

        // Assert
        Assert.Single(changes, c => c.Property.Subject == person && c.Property.Name == nameof(Person.FullName));
    }

    [Fact]
    public void WhenSubjectLeavesItsLastGraphWhichHasNoDerivedDetection_ThenItsDerivedPropertiesDetach()
    {
        // Arrange
        var tracked = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var lifecycleOnly = InterceptorSubjectContext.Create().WithLifecycle();
        var trackedRoot = new Person(tracked);
        var otherRoot = new Person(lifecycleOnly);
        var person = new Person { FirstName = "a" };
        trackedRoot.Mother = person;
        otherRoot.Mother = person;

        // Act
        trackedRoot.Mother = null;
        otherRoot.Mother = null;

        // Assert
        Assert.Empty(new PropertyReference(person, nameof(Person.FirstName)).GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenGetterWritesWhatItReads_ThenAttachAndRecalculationEndAtTheLimitAndLaterWritesStillPublish()
    {
        // Arrange: the getter writes what it reads. Another thread writes Anchor during the first attach
        // evaluation, so the attach evaluates again once it linked Anchor; from then on every own write of the
        // linked Anchor requests a recalculation on the evaluating thread.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var evaluations = 0;
        var isWrittenByOtherThread = false;
        machine.Worker.Evaluator = _ =>
        {
            var value = root.Anchor;
            if (++evaluations == 1)
            {
                isWrittenByOtherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Anchor = 500).Wait(Timeout);
            }

            root.Anchor = value + 1;
            return value;
        };
        var changes = new List<SubjectPropertyChange>();
        using var traceOutput = new StringWriter();
        using var listener = new TextWriterTraceListener(traceOutput);
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            root.Machine = machine;
            context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
            machine.Worker.Evaluator = _ => root.Anchor;
            root.Anchor = 1000;

            // Assert: the attach evaluation never stabilizes and commits its last value; the recalculation
            // requested after the attach never stabilizes either and ends at the limit, and a later write still
            // recalculates.
            Assert.True(isWrittenByOtherThread);
            Assert.Contains(changes, c => c.Property.Subject == machine.Worker && c.GetNewValue<int>() == 1000);
            listener.Flush();
            var trace = traceOutput.ToString();
            Assert.Contains("exhausted while attaching", trace);
            Assert.Contains("exhausted for", trace);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task WhenGetterWritesASubjectItCreates_ThenEachCommitTakesTwoEvaluationsAndLaterWritesPublish()
    {
        // Arrange: the getter reads and then writes a property of a subject it creates, a property no derived
        // property is linked to, so the write requests nothing. Each evaluation writes after it read, so each
        // commit follows exactly one further evaluation.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var evaluations = 0;
        machine.Worker.Evaluator = _ =>
        {
            Interlocked.Increment(ref evaluations);
            var summary = new EvaluatorConfiguration();
            summary.Value += root.Anchor;
            return summary.Value;
        };
        var changes = new List<SubjectPropertyChange>();

        // Act
        await DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Machine = machine).WaitAsync(Timeout);
        var evaluationsAfterAttach = Volatile.Read(ref evaluations);
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        await DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Anchor = 5).WaitAsync(Timeout);

        // Assert
        Assert.Equal(2, evaluationsAfterAttach);
        Assert.Equal(4, Volatile.Read(ref evaluations));
        var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker);
        Assert.Equal(0, change.GetOldValue<int>());
        Assert.Equal(5, change.GetNewValue<int>());
    }

    [Fact]
    public async Task WhenAnotherThreadWritesBetweenTheGettersReadAndItsOwnWrite_ThenTheGetterIsEvaluatedAgain()
    {
        // Arrange: during the first evaluation another thread writes the property the getter read and writes
        // next, so the getter's own write does not follow the version the evaluation read. The second evaluation
        // writes after its read on its own thread only, which is answered by exactly one further evaluation.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var evaluations = 0;
        var isWrittenByOtherThread = false;
        machine.Worker.Evaluator = _ =>
        {
            var evaluation = Interlocked.Increment(ref evaluations);
            var summary = new EvaluatorConfiguration();
            var value = summary.Value;
            if (evaluation == 1)
            {
                isWrittenByOtherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => summary.Value = 100).Wait(Timeout);
            }

            summary.Value = value + root.Anchor + 1;
            return summary.Value;
        };

        // Act
        await DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Machine = machine).WaitAsync(Timeout);

        // Assert
        Assert.True(isWrittenByOtherThread);
        Assert.Equal(3, Volatile.Read(ref evaluations));
    }

    [Fact]
    public void WhenGetterWritesASourceItReadBeforeItIsLinkedAtAttach_ThenTheAttachCommitsTheValueOfAnotherEvaluation()
    {
        // Arrange: the first evaluation reads Anchor and then writes it. Anchor is not linked yet, so the write
        // requests no recalculation.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var evaluations = 0;
        machine.Worker.Evaluator = _ =>
        {
            var value = root.Anchor;
            if (++evaluations == 1)
            {
                root.Anchor = value + 1;
            }

            return value;
        };

        // Act
        root.Machine = machine;

        // Assert
        Assert.Equal(2, evaluations);
        Assert.Equal(1, machine.Worker.Result);
        Assert.Equal(1, new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public void WhenRecalculationNewlyReadsASourceAndWritesIt_ThenTheValueOfAnotherEvaluationIsPublished()
    {
        // Arrange: once Data is set, the getter reads Anchor, which is not linked yet, and writes it once.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var isWritten = false;
        machine.Worker.Evaluator = _ =>
        {
            if (root.Data is null)
            {
                return -1;
            }

            var value = root.Anchor;
            if (!isWritten)
            {
                isWritten = true;
                root.Anchor = 10;
            }

            return value;
        };
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        root.Data = [1];

        // Assert
        var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker);
        Assert.Equal(-1, change.GetOldValue<int>());
        Assert.Equal(10, change.GetNewValue<int>());
        Assert.Equal(10, machine.Worker.Result);
    }

    [Fact]
    public void WhenANestedRecalculationWritesASourceTheOuterGetterRead_ThenTheOuterAttachCommitsTheValueOfAnotherEvaluation()
    {
        // Arrange: the inner derived property follows the other root's Anchor and writes the root's Anchor. The
        // outer getter reads the root's Anchor and then writes the other root's Anchor once, which recalculates
        // the inner derived property on this thread, nested in the outer evaluation.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var otherRoot = new EvaluatorRoot(context);
        var inner = new EvaluatorMachine();
        inner.Worker.Evaluator = _ =>
        {
            var value = otherRoot.Anchor;
            if (value == 1)
            {
                root.Anchor = 10;
            }

            return value;
        };
        otherRoot.Machine = inner;
        var outer = new EvaluatorMachine();
        var isWritten = false;
        outer.Worker.Evaluator = _ =>
        {
            var value = root.Anchor;
            if (!isWritten)
            {
                isWritten = true;
                otherRoot.Anchor = 1;
            }

            return value;
        };

        // Act
        root.Machine = outer;

        // Assert
        Assert.Equal(1, inner.Worker.Result);
        Assert.Equal(10, outer.Worker.Result);
        Assert.Equal(10, new PropertyReference(outer.Worker, nameof(EvaluatorWorker.Result)).TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public void WhenAttachFailsOutsideTheGetter_ThenThePropertyIsReleasedAndItsFirstSuccessIsPublished()
    {
        // Arrange: the time source writes Anchor and fails when the attach resolves the timestamp of its first
        // value, before the attach committed its links or a value. The worker's second derived property attaches
        // after the first one.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var testThread = Environment.CurrentManagedThreadId;
        var isArmed = false;
        var evaluations = 0;
        machine.Worker.Evaluator = _ =>
        {
            evaluations++;
            isArmed = evaluations == 1;
            return root.Anchor;
        };
        var publishedValues = new List<(object? OldValue, object? NewValue)>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == machine.Worker)
            {
                publishedValues.Add((change.GetOldValue<object?>(), change.GetNewValue<object?>()));
            }
        });
        using var timeSource = ReplaceTimeSource(() =>
        {
            if (!isArmed || Environment.CurrentManagedThreadId != testThread)
            {
                return DateTimeOffset.UtcNow;
            }

            isArmed = false;
            root.Anchor = 1000;
            throw new InvalidOperationException("Time source failed.");
        });
        machine.Worker.ObjectEvaluator = _ => root.Data;
        using var traceOutput = new StringWriter();
        using var listener = new TextWriterTraceListener(traceOutput);
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            var exception = Record.Exception(() => root.Machine = machine);
            timeSource.Dispose();
            var evaluationsAfterAttach = evaluations;
            root.Anchor = 2000;
            var data = new byte[1];
            root.Data = data;

            // Assert: the attach completes and traces the failure. The recalculation after the failed attach
            // published its value from the type's default, and a later write publishes from that value.
            Assert.Null(exception);
            listener.Flush();
            Assert.Contains("Time source failed.", traceOutput.ToString());
            Assert.Equal(2, evaluationsAfterAttach);
            Assert.Equal((0, 1000), publishedValues[0]);
            Assert.Equal((1000, 2000), publishedValues[1]);
            Assert.Equal((null, data), publishedValues[2]);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void WhenTimeSourceRecalculatesAnotherDerivedPropertyDuringAttach_ThenTheAttachKeepsItsDependencies()
    {
        // Arrange: once the first attach evaluation ran, the next time source call on this thread writes FirstName,
        // which recalculates FullName on this thread.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var person = new Person(context) { FirstName = "a" };
        var machine = new EvaluatorMachine();
        var testThread = Environment.CurrentManagedThreadId;
        var isArmed = false;
        var hasWritten = false;
        machine.Worker.Evaluator = _ =>
        {
            isArmed = !hasWritten;
            return root.Anchor;
        };
        var changes = new List<SubjectPropertyChange>();
        using var timeSource = ReplaceTimeSource(() =>
        {
            if (isArmed && Environment.CurrentManagedThreadId == testThread)
            {
                isArmed = false;
                hasWritten = true;
                person.FirstName = "b";
            }

            return DateTimeOffset.UtcNow;
        });

        // Act
        root.Machine = machine;
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        root.Anchor = 5;

        // Assert
        Assert.True(hasWritten);
        Assert.Contains(
            new PropertyReference(root, nameof(EvaluatorRoot.Anchor)),
            new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).GetRequiredProperties().ToArray());
        var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker);
        Assert.Equal(5, change.GetNewValue<int>());
    }

    [Fact]
    public void WhenTimeSourceFailsWhileALoopPublishesATakenOverForeignRequest_ThenTheRequestIsCommittedAndThePropertyReleased()
    {
        // Arrange: the first publication lets another thread write Anchor and throws. That thread's time source
        // returns no time, so the request carries no timestamp. The loop that takes it over evaluates and commits
        // it, and its publication resolves the time source, which throws once: the publication fails as if an
        // observer threw.
        var testThread = Environment.CurrentManagedThreadId;
        var otherThread = 0;
        var isArmed = false;
        var isWrittenByOtherThread = false;
        var publications = 0;
        var publishedValues = new List<object?>();
        EvaluatorRoot? root = null;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        context.AddService(new PublicationObserver((_, value) =>
        {
            publishedValues.Add(value);
            if (++publications == 1)
            {
                isWrittenByOtherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    Volatile.Write(ref otherThread, Environment.CurrentManagedThreadId);
                    root!.Anchor = 50;
                }).Wait(Timeout);
                isArmed = true;
                throw new InvalidOperationException("Publication 1.");
            }
        }));
        root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        root.Machine = machine;
        using var timeSource = ReplaceTimeSource(() =>
        {
            var thread = Environment.CurrentManagedThreadId;
            if (thread == Volatile.Read(ref otherThread))
            {
                return DateTimeOffset.MinValue;
            }

            if (isArmed && thread == testThread)
            {
                isArmed = false;
                throw new InvalidOperationException("Time source failed.");
            }

            return DateTimeOffset.UtcNow;
        });

        // Act
        var exception = Record.Exception(() => root.Anchor = 1);
        var committedValue = new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).TryGetDerivedPropertyData()?.LastKnownValue;
        root.Anchor = 60;

        // Assert
        Assert.Equal("Publication 1.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.True(isWrittenByOtherThread);
        Assert.False(isArmed);
        Assert.Equal(50, committedValue);
        Assert.Equal(new object?[] { 1, 60 }, publishedValues);
    }

    [Fact]
    public async Task WhenAnotherThreadWritesDuringEveryEvaluationAcrossIterationLimits_ThenNoRequestIsLostAndLoopsDoNotNest()
    {
        // Arrange: during each evaluation, a dedicated thread writes Anchor and the getter waits until that write
        // requested the recalculation, so every owner loop reaches the iteration limit with a foreign request.
        const int writeCount = 250;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        root.Machine = machine;
        using var writeRequested = new SemaphoreSlim(0);
        using var writeDone = new SemaphoreSlim(0);
        var writes = 0;
        var hasTimedOut = false;
        var firstHandlerFrames = 0;
        var maxHandlerFrames = 0;
        machine.Worker.Evaluator = _ =>
        {
            var handlerFrames = CountHandlerFrames();
            firstHandlerFrames = firstHandlerFrames == 0 ? handlerFrames : firstHandlerFrames;
            maxHandlerFrames = Math.Max(maxHandlerFrames, handlerFrames);
            var value = root.Anchor;
            if (Volatile.Read(ref writes) < writeCount)
            {
                writeRequested.Release();
                hasTimedOut |= !writeDone.Wait(Timeout);
            }

            return value;
        };
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            while (Volatile.Read(ref writes) < writeCount && writeRequested.Wait(Timeout))
            {
                root.Anchor++;
                Interlocked.Increment(ref writes);
                writeDone.Release();
            }
        });
        var lastValue = 0;
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == machine.Worker)
            {
                lastValue = change.GetNewValue<int>();
            }
        });

        // Act
        root.Anchor = 1;
        await writer.WaitAsync(Timeout);

        // Assert: a loop after an iteration limit runs in the frame of the first one, not nested in it.
        Assert.False(hasTimedOut);
        Assert.Equal(writeCount, writes);
        Assert.Equal(root.Anchor, lastValue);
        Assert.True(maxHandlerFrames <= firstHandlerFrames, $"Handler frames: first {firstHandlerFrames}, max {maxHandlerFrames}.");
    }

    [Fact]
    public void WhenObserverThrowsAgainWhileAnotherThreadsWriteIsPending_ThenThatWriteIsPublished()
    {
        // Arrange: the first publication writes Anchor on the owner's thread and throws, so one further loop
        // runs. Its publication lets another thread write Anchor and throws as well.
        var publications = 0;
        var isWrittenByOtherThread = false;
        var publishedValues = new List<object?>();
        EvaluatorRoot? root = null;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        context.AddService(new PublicationObserver((_, value) =>
        {
            publishedValues.Add(value);
            publications++;
            if (publications == 1)
            {
                root!.Anchor = 10;
                throw new InvalidOperationException("Publication 1.");
            }

            if (publications == 2)
            {
                isWrittenByOtherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root!.Anchor = 50).Wait(Timeout);
                throw new InvalidOperationException("Publication 2.");
            }
        }));
        root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        root.Machine = machine;

        // Act
        var exception = Record.Exception(() => root.Anchor = 1);

        // Assert
        Assert.Equal("Publication 1.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.True(isWrittenByOtherThread);
        Assert.Equal(new object?[] { 1, 10, 50 }, publishedValues);
    }

    [Fact]
    public void WhenPropertyLifecycleHandlerThrowsWhileSubjectLeavesItsLastGraph_ThenItsDerivedPropertiesDetach()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);
        var person = new Person { FirstName = "a" };
        root.Mother = person;
        context.AddService(new ThrowingDetachHandler(person));

        // Act
        var exception = Record.Exception(() => root.Mother = null);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.DoesNotContain(
            new PropertyReference(person, nameof(Person.FullName)),
            new PropertyReference(person, nameof(Person.FirstName)).GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenObserverWritesAndThrowsOnEveryPublication_ThenFirstExceptionPropagatesAfterOneFurtherLoop()
    {
        // Arrange
        var publications = 0;
        var isThrowing = true;
        var publishedValues = new List<object?>();
        EvaluatorRoot? root = null;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        context.AddService(new PublicationObserver((_, value) =>
        {
            publishedValues.Add(value);
            if (isThrowing)
            {
                publications++;
                root!.Anchor++;
                throw new InvalidOperationException($"Publication {publications}.");
            }
        }));
        root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        root.Machine = machine;

        // Act
        var exception = Record.Exception(() => root.Anchor = 1);
        isThrowing = false;
        root.Anchor = 100;

        // Assert: the request the second publication left is dropped, and the property is released for later writes.
        Assert.Equal("Publication 1.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(2, publications);
        Assert.Equal(100, publishedValues[^1]);
    }

    [Fact]
    public void WhenObserverThrowsOnThePublicationOfEveryDependent_ThenEveryDependentOfTheWriteIsRecalculated()
    {
        // Arrange: Upper and Doubled both read FirstName and do not read each other, so neither publication
        // requests the other, whatever order the write requests them in.
        var isThrowing = false;
        var publishedProperties = new List<string>();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        context.AddService(new PublicationObserver((property, _) =>
        {
            if (isThrowing && property.Name is "Upper" or "Doubled")
            {
                publishedProperties.Add(property.Name);
                throw new InvalidOperationException($"Publication of {property.Name}.");
            }
        }));
        var person = new Person(context) { FirstName = "a", LastName = "c" };
        var registered = person.TryGetRegisteredSubject()!;
        var upper = registered.AddDerivedProperty<string?>("Upper", subject => ((Person)subject).FirstName?.ToUpperInvariant()).Reference;
        var doubled = registered.AddDerivedProperty<string?>("Doubled", subject => ((Person)subject).FirstName + ((Person)subject).FirstName).Reference;
        isThrowing = true;

        // Act
        var exception = Record.Exception(() => person.FirstName = "b");

        // Assert: the first exception propagates after both dependents were requested.
        Assert.StartsWith("Publication of ", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(["Doubled", "Upper"], publishedProperties.Order());
        Assert.Equal("B", upper.TryGetDerivedPropertyData()?.LastKnownValue);
        Assert.Equal("bb", doubled.TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public void WhenLifecycleHandlerAndEveryDependentsObserverThrowOnAStructuralWrite_ThenHandlersExceptionPropagatesAndEveryDependentIsRecalculated()
    {
        // Arrange: both derived properties of the worker read Spare; the attach of the spare configuration fails
        // after the value landed.
        var publishedProperties = new List<string>();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        context.AddService(new PublicationObserver((property, _) =>
        {
            publishedProperties.Add(property.Metadata.Name);
            throw new InvalidOperationException("Publication failed.");
        }));
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Spare is null ? 0 : 1;
        machine.Worker.ObjectEvaluator = _ => root.Spare;
        root.Machine = machine;
        var spare = new EvaluatorConfiguration();
        context.AddService(new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == spare)
            {
                throw new InvalidOperationException("Lifecycle handler failed.");
            }
        }));

        // Act
        var exception = Record.Exception(() => root.Spare = spare);

        // Assert
        Assert.Equal("Lifecycle handler failed.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal([nameof(EvaluatorWorker.ObjectResult), nameof(EvaluatorWorker.Result)], publishedProperties.Order());
        Assert.Equal(1, new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).TryGetDerivedPropertyData()?.LastKnownValue);
        Assert.Same(spare, new PropertyReference(machine.Worker, nameof(EvaluatorWorker.ObjectResult)).TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public void WhenAPublicationPublishesAnotherDerivedPropertyBeforeItsObserverRuns_ThenEachVersionMovesOnceAndTheReaderFollowsBoth()
    {
        // Arrange: the publication of Upper writes LastName in its chain, which publishes Lower before the write
        // observer of Upper's publication runs. Both reads Upper and Lower.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var isWritingLastName = false;
        Person? person = null;
        context.AddService(new PublicationObserver((property, _) =>
        {
            if (isWritingLastName && property.Name == "Upper")
            {
                isWritingLastName = false;
                person!.LastName = "X";
            }
        }));
        person = new Person(context) { FirstName = "a", LastName = "c" };
        var registered = person.TryGetRegisteredSubject()!;
        var upper = registered.AddDerivedProperty<string?>("Upper", subject => ((Person)subject).FirstName?.ToUpperInvariant()).Reference;
        var lower = registered.AddDerivedProperty<string?>("Lower", subject => ((Person)subject).LastName?.ToLowerInvariant()).Reference;
        var both = registered.AddDerivedProperty<string>(
            "Both", subject => $"{subject.Properties["Upper"].GetValue?.Invoke(subject)}{subject.Properties["Lower"].GetValue?.Invoke(subject)}").Reference;
        var upperVersion = upper.TryGetDerivedPropertyData()!.Version;
        var lowerVersion = lower.TryGetDerivedPropertyData()!.Version;
        isWritingLastName = true;

        // Act
        person.FirstName = "b";

        // Assert
        Assert.False(isWritingLastName);
        Assert.Equal(upperVersion + 1, upper.TryGetDerivedPropertyData()!.Version);
        Assert.Equal(lowerVersion + 1, lower.TryGetDerivedPropertyData()!.Version);
        Assert.Equal("Bx", both.TryGetDerivedPropertyData()?.LastKnownValue);
        Assert.Contains(upper, both.GetRequiredProperties().ToArray());
        Assert.Contains(lower, both.GetRequiredProperties().ToArray());
    }

    private static TimeSource ReplaceTimeSource(Func<DateTimeOffset> timestampFunction)
    {
        var timeSource = new TimeSource(SubjectChangeContext.GetTimestampFunction);
        SubjectChangeContext.GetTimestampFunction = timestampFunction;
        return timeSource;
    }

    private static int CountHandlerFrames()
    {
        return new StackTrace().GetFrames().Count(frame => frame.GetMethod()?.DeclaringType == typeof(DerivedPropertyOwnership));
    }

    /// <summary>Restores the previous time source when disposed.</summary>
    private sealed class TimeSource(Func<DateTimeOffset> previousTimestampFunction) : IDisposable
    {
        public void Dispose() => SubjectChangeContext.GetTimestampFunction = previousTimestampFunction;
    }

    /// <summary>Runs an action after each publication of a derived property's recalculated value.</summary>
    private sealed class PublicationObserver(Action<PropertyReference, object?> onPublished) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (context.IsDerivedRecalculation)
            {
                onPublished(context.Property, context.NewValue);
            }
        }
    }

    private sealed class ThrowingDetachHandler(IInterceptorSubject subject) : IPropertyLifecycleHandler
    {
        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            // Only detaches fail.
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
            if (change.Subject == subject)
            {
                throw new InvalidOperationException("Detach handler failed.");
            }
        }
    }
}
