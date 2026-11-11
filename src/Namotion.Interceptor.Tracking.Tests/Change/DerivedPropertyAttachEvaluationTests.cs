using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Counts getter evaluations, which a concurrent write in another test can raise through the stabilization loop.
[Collection(DerivedPropertyWriteGenerationCollection.Name)]
public class DerivedPropertyAttachEvaluationTests
{
    [Fact]
    public void WhenSingleSubjectIsAttached_ThenDerivedPropertyIsEvaluatedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        var device = new SiblingReadingDevice();

        // Act
        machine.Device = device;

        // Assert
        Assert.Equal(1, device.EvaluationCount);
    }

    [Fact]
    public void WhenSubtreeIsAttached_ThenOnlyDerivedPropertyReadingThroughSubjectPropertiesIsEvaluatedAgain()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();

        // Act
        root.Machines = [machine, new SiblingReadingMachine()];

        // Assert
        Assert.Equal(2, machine.Device.EvaluationCount);
        Assert.Equal(1, machine.Configuration.EvaluationCount);
    }

    [Fact]
    public void WhenObserverWritesGraphWhileAttachPublishesChangedDerivedValue_ThenBothWritesComplete()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(5);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var source = new EvaluatorConfiguration(context);
        var machine = new EvaluatorMachine();
        using var observerEntered = new ManualResetEventSlim();
        using var secondEvaluationReached = new ManualResetEventSlim();
        using var observerWritesGraph = new ManualResetEventSlim();
        machine.Worker.Evaluator = worker =>
        {
            // Changes between the two evaluations: the configuration has no parent before it is attached.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            var result = configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? 1 : 0;
            if (++worker.EvaluationCount == 2)
            {
                secondEvaluationReached.Set();
                observerWritesGraph.Wait(timeout);
            }

            return result;
        };

        // Holds the dispatch lock of the observable while it writes the graph, which takes the lifecycle lock.
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, source))
            {
                observerEntered.Set();
                secondEvaluationReached.Wait(timeout);
                observerWritesGraph.Set();
                root.Spare = new EvaluatorConfiguration();
            }
        });
        var valueThread = new Thread(() => source.Value = 1) { IsBackground = true };
        var attachThread = new Thread(() => root.Machine = machine) { IsBackground = true };

        // Act
        valueThread.Start();
        Assert.True(observerEntered.Wait(timeout));
        attachThread.Start();
        var attachCompleted = attachThread.Join(timeout);
        var valueCompleted = valueThread.Join(timeout);

        // Assert
        Assert.True(attachCompleted && valueCompleted, $"attachCompleted={attachCompleted}, valueCompleted={valueCompleted}");
        Assert.Equal(1, machine.Worker.Result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WhenEvaluationDetachesItsOwnSubject_ThenAncestorDoesNotKeepTheDetachedDerivedProperty(int detachingEvaluation)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var worker = machine.Worker;
        worker.Evaluator = evaluatedWorker =>
        {
            var parent = evaluatedWorker.TryGetFirstParent<EvaluatorMachine>();

            // Read in every evaluation, so that the property is evaluated again once the configuration is attached.
            _ = parent?.Configuration;
            if (++evaluatedWorker.EvaluationCount == detachingEvaluation && parent is not null)
            {
                parent.Worker = new EvaluatorWorker();
            }

            return root.Anchor;
        };

        // Act
        root.Machine = machine;

        // Assert
        var usedByProperties = new PropertyReference(root, nameof(EvaluatorRoot.Anchor)).GetUsedByProperties().Items.ToArray();
        Assert.DoesNotContain(usedByProperties, property => ReferenceEquals(property.Subject, worker));
    }

    [Fact]
    public void WhenDetachHandlerAttachesAnotherRoot_ThenChangedDerivedValueIsPublishedOutsideTheLifecycleLock()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var outgoingRoot = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var incomingRoot = new EvaluatorRoot { Machine = machine };
        machine.Worker.Evaluator = worker =>
        {
            // Changes between the two evaluations: the configuration has no parent before it is attached.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? 1 : 0;
        };

        var interceptor = context.GetService<LifecycleInterceptor>();
        interceptor.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, outgoingRoot))
            {
                ((IInterceptorSubject)incomingRoot).Context.AddFallbackContext(context);
            }
        };

        var lifecycleLockWasFree = new List<bool>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, machine.Worker) && change.Property.Name == nameof(EvaluatorWorker.Result))
            {
                // Another thread gets the lifecycle lock only if this thread does not hold it.
                var otherThread = Task.Run(() => interceptor.TryRunWhileAttached(incomingRoot, () => { }));
                lifecycleLockWasFree.Add(otherThread.Wait(TimeSpan.FromSeconds(2)));
            }
        });

        // Act
        ((IInterceptorSubject)outgoingRoot).Context.RemoveFallbackContext(context);

        // Assert
        Assert.True(Assert.Single(lifecycleLockWasFree));
    }
}
