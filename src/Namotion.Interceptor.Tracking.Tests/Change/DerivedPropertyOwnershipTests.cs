using System.Diagnostics;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(DerivedPropertyTraceCollection.Name)]
public class DerivedPropertyOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root.Anchor = 1;

        // Assert
        var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker && c.Property.Name == nameof(EvaluatorWorker.Result));
        Assert.Equal(0, change.GetOldValue<int>());
        Assert.Equal(10, change.GetNewValue<int>());
    }

    [Fact]
    public void WhenSubjectIsAttachedToSecondGraph_ThenItsPublishedValueIsNotReplacedSilently()
    {
        // Arrange
        var context1 = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var context2 = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root1 = new Person(context1);
        var root2 = new Person(context2);
        var person = new Person { FirstName = "a" };
        root1.Mother = person;
        var changes = new List<SubjectPropertyChange>();
        context1.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root2.Mother = person;
        person.FirstName = "b";

        // Assert
        var change = Assert.Single(changes, c => c.Property.Subject == person && c.Property.Name == nameof(Person.FullName));
        Assert.Equal("a", change.GetOldValue<string>());
        Assert.Equal("b", change.GetNewValue<string>());
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
        var changes = new List<SubjectPropertyChange>();
        context1.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
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
        var changes = new List<SubjectPropertyChange>();
        context1.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root2.Mother = null;
        person.FirstName = "b";

        // Assert
        Assert.Single(changes, c => c.Property.Subject == person && c.Property.Name == nameof(Person.FullName));
    }

    [Fact]
    public void WhenDerivedPropertyIsDetachedDirectlyWhileSubjectIsAttached_ThenItsLinksAreRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { FirstName = "a" };
        var fullName = new PropertyReference(person, nameof(Person.FullName));

        // Act
        person.DetachSubjectProperty(fullName);

        // Assert: FullNameWithPrefix still reads FirstName through FullName's getter.
        Assert.DoesNotContain(fullName, new PropertyReference(person, nameof(Person.FirstName)).GetUsedByProperties().Items.ToArray());
        Assert.Empty(fullName.GetRequiredProperties().ToArray());
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
    public void WhenGetterWritesWhatItReadsDuringAttach_ThenAttachCompletesAndLaterWritesStillPublish()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor++;
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
            // requested after the attach never stabilizes either, and a later write still recalculates.
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
    public void WhenAttachFailsOutsideTheGetter_ThenThePropertyIsReleasedAndRecalculates()
    {
        // Arrange: the getter writes what it reads, so the attach reaches the iteration limit. The trace listener
        // makes the getter stable, writes Anchor (a request the attach owns) and throws on that warning.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var stableEvaluations = 0;
        machine.Worker.Evaluator = _ => root.Anchor++;
        var changes = new List<SubjectPropertyChange>();
        using var listener = new ActionTraceListener("exhausted while attaching", () =>
        {
            machine.Worker.Evaluator = _ =>
            {
                stableEvaluations++;
                return root.Anchor;
            };
            root.Anchor = 1000;
            throw new InvalidOperationException("Trace failed.");
        });
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            var exception = Record.Exception(() => root.Machine = machine);
            Trace.Listeners.Remove(listener);
            var evaluationsAfterAttach = stableEvaluations;
            context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
            root.Anchor = 2000;

            // Assert: the request the failed attach owned ran after the failure, and a later write publishes from its value.
            Assert.Equal("Trace failed.", Assert.IsType<InvalidOperationException>(exception).Message);
            Assert.Equal(1, evaluationsAfterAttach);
            var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker);
            Assert.Equal(1000, change.GetOldValue<int>());
            Assert.Equal(2000, change.GetNewValue<int>());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void WhenWritesKeepArrivingAcrossIterationLimits_ThenOwnerLoopsRunWithoutNesting()
    {
        // Arrange: every publication writes Anchor again, so each owner loop reaches the iteration limit; the
        // trace listener writes Anchor on that warning, so a request is pending when the loop releases ownership.
        const int publicationCount = 2000;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        root.Machine = machine;
        var publications = 0;
        var lastValue = 0;
        var firstHandlerFrames = 0;
        var maxHandlerFrames = 0;
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject != machine.Worker)
            {
                return;
            }

            var handlerFrames = CountHandlerFrames();
            firstHandlerFrames = publications == 0 ? handlerFrames : firstHandlerFrames;
            maxHandlerFrames = Math.Max(maxHandlerFrames, handlerFrames);
            lastValue = change.GetNewValue<int>();
            if (++publications < publicationCount)
            {
                root.Anchor++;
            }
        });
        var limitsReached = 0;
        using var listener = new ActionTraceListener("exhausted for", () =>
        {
            limitsReached++;
            if (publications < publicationCount)
            {
                root.Anchor++;
            }
        });
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            root.Anchor = 1;
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        // Assert: a loop after an iteration limit runs in the frame of the first one, not nested in it.
        Assert.Equal(publicationCount, publications);
        Assert.True(limitsReached >= 10, $"Iteration limits reached: {limitsReached}.");
        Assert.True(maxHandlerFrames <= firstHandlerFrames, $"Handler frames: first {firstHandlerFrames}, max {maxHandlerFrames}.");
        Assert.Equal(root.Anchor, lastValue);
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

    private static int CountHandlerFrames()
    {
        return new StackTrace().GetFrames().Count(frame => frame.GetMethod()?.DeclaringType == typeof(DerivedPropertyChangeHandler));
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

    /// <summary>Runs an action for a trace message that contains a text, on the thread that created the listener.</summary>
    private sealed class ActionTraceListener(string text, Action action) : TraceListener
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (message is not null && message.Contains(text) && Environment.CurrentManagedThreadId == _threadId)
            {
                action();
            }
        }

        public override void Write(string? message)
        {
            // Only trace events are inspected.
        }

        public override void WriteLine(string? message)
        {
            // Only trace events are inspected.
        }
    }
}
