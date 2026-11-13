using System.Diagnostics;
using System.Reactive.Concurrency;
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
        // Arrange: the getter takes a lock that the writer of its dependency holds while it writes. The
        // attach evaluates without holding lock(data), so the writer's recalculation request never waits
        // for the attach, and the attach never waits for the writer while it holds lock(data).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var other = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var sync = new object();
        using var firstPassRead = new ManualResetEventSlim();
        using var unrelatedWritten = new ManualResetEventSlim();
        using var writerHoldsLock = new ManualResetEventSlim();
        var evaluations = 0;
        machine.Worker.Evaluator = _ =>
        {
            var evaluation = Interlocked.Increment(ref evaluations);
            if (evaluation == 1)
            {
                var anchor = root.Anchor;
                firstPassRead.Set();
                unrelatedWritten.Wait(Timeout);
                return anchor;
            }

            if (evaluation == 2)
            {
                writerHoldsLock.Wait(Timeout);
            }

            lock (sync)
            {
                return root.Anchor;
            }
        };

        // Act
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Machine = machine);
        Assert.True(firstPassRead.Wait(Timeout));
        other.Anchor = 1;
        unrelatedWritten.Set();
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
}
