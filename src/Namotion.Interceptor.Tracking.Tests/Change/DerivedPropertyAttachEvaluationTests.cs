using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
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
    public async Task WhenObserverWritesGraphWhileAttachPublishesChangedDerivedValue_ThenBothWritesComplete()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var source = new EvaluatorConfiguration(context);
        var machine = new EvaluatorMachine();
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEvaluationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerWritesGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        machine.Worker.Evaluator = worker =>
        {
            var result = EvaluatorWorker.ReadConfigurationOnceAttached(worker);
            if (++worker.EvaluationCount == 2)
            {
                secondEvaluationReached.SetResult();
                observerWritesGraph.Task.Wait(TestTimeouts.Default);
            }

            return result;
        };

        // Holds the dispatch lock of the observable while it writes the graph, which takes the lifecycle lock.
        var publishedResults = new List<int>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, source))
            {
                observerEntered.SetResult();
                secondEvaluationReached.Task.Wait(TestTimeouts.Default);
                observerWritesGraph.SetResult();
                root.Spare = new EvaluatorConfiguration();
            }
            else if (ReferenceEquals(change.Property.Subject, machine.Worker))
            {
                publishedResults.Add(change.GetNewValue<int>());
            }
        });

        // Act
        var valueWrite = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { source.Value = 1; });
        await observerEntered.Task.WaitAsync(TestTimeouts.Default);
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Machine = machine; });
        await Task.WhenAll(valueWrite, attach).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal([1], publishedResults);
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
        Assert.NotSame(worker, machine.Worker);
        var usedByProperties = new PropertyReference(root, nameof(EvaluatorRoot.Anchor)).GetUsedByProperties().Items.ToArray();
        Assert.DoesNotContain(usedByProperties, property => ReferenceEquals(property.Subject, worker));
    }

    [Fact]
    public async Task WhenGetterWaitsForLockOfThreadWritingItsDependency_ThenBothComplete()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var getterLock = new object();
        var firstEvaluationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEvaluationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        machine.Worker.Evaluator = worker =>
        {
            var evaluation = ++worker.EvaluationCount;
            if (evaluation == 2)
            {
                writerHoldsLock.Task.Wait(TestTimeouts.Default);
                secondEvaluationReached.SetResult();
            }

            int result;
            lock (getterLock)
            {
                result = root.Anchor + worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
            }

            if (evaluation == 1)
            {
                firstEvaluationDone.SetResult();
            }

            return result;
        };

        // Act: the writer holds the lock the second evaluation waits for, and writes a dependency recorded by the first.
        var write = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            firstEvaluationDone.Task.Wait(TestTimeouts.Default);
            lock (getterLock)
            {
                writerHoldsLock.SetResult();
                secondEvaluationReached.Task.Wait(TestTimeouts.Default);
                root.Anchor = 5;
            }
        });
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Machine = machine; });
        await Task.WhenAll(write, attach).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(5, machine.Worker.Result);
        Assert.Equal(5, new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public void WhenDetachHandlerAttachesAnotherRoot_ThenChangedDerivedValueIsPublishedOutsideTheLifecycleLock()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var outgoingRoot = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var incomingRoot = new EvaluatorRoot { Machine = machine };
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;

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
                var otherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
                    () => interceptor.TryRunWhileAttached(incomingRoot, () => { }));
                lifecycleLockWasFree.Add(otherThread.Wait(TestTimeouts.Default));
            }
        });

        // Act
        ((IInterceptorSubject)outgoingRoot).Context.RemoveFallbackContext(context);

        // Assert
        Assert.True(Assert.Single(lifecycleLockWasFree));
    }

    [Fact]
    public void WhenRootIsInTwoContexts_ThenChangedDerivedValueIsPublishedOutsideBothLifecycleLocks()
    {
        // Arrange
        var firstContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var secondContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot();
        ((IInterceptorSubject)root).Context.AddFallbackContext(firstContext);
        ((IInterceptorSubject)root).Context.AddFallbackContext(secondContext);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;

        LifecycleInterceptor[] interceptors = [firstContext.GetService<LifecycleInterceptor>(), secondContext.GetService<LifecycleInterceptor>()];
        var lifecycleLocksWereFree = new List<bool>();
        using var subscription = firstContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, machine.Worker) && change.Property.Name == nameof(EvaluatorWorker.Result))
            {
                // Another thread gets both lifecycle locks only if this thread holds neither.
                var otherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    foreach (var interceptor in interceptors)
                    {
                        interceptor.TryRunWhileAttached(root, () => { });
                    }
                });
                lifecycleLocksWereFree.Add(otherThread.Wait(TestTimeouts.Default));
            }
        });

        // Act
        root.Machine = machine;

        // Assert
        Assert.NotEmpty(lifecycleLocksWereFree);
        Assert.All(lifecycleLocksWereFree, Assert.True);
    }

    [Fact]
    public void WhenGraphIsAttachedToTwoContextsAtOnce_ThenChangedDerivedValueIsPublishedOutsideBothLifecycleLocks()
    {
        // Arrange: outside a write, the attach of one interceptor is nested in the attach of the other.
        var firstContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var secondContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var bothContexts = InterceptorSubjectContext.Create();
        bothContexts.AddFallbackContext(firstContext);
        bothContexts.AddFallbackContext(secondContext);
        var machine = new EvaluatorMachine();
        var root = new EvaluatorRoot { Machine = machine };
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;

        LifecycleInterceptor[] interceptors = [firstContext.GetService<LifecycleInterceptor>(), secondContext.GetService<LifecycleInterceptor>()];
        var lifecycleLocksWereFree = new List<bool>();
        using var subscription = firstContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, machine.Worker) && change.Property.Name == nameof(EvaluatorWorker.Result))
            {
                // Another thread gets both lifecycle locks only if this thread holds neither.
                var otherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    foreach (var interceptor in interceptors)
                    {
                        interceptor.TryRunWhileAttached(root, () => { });
                    }
                });
                lifecycleLocksWereFree.Add(otherThread.Wait(TestTimeouts.Default));
            }
        });

        // Act
        ((IInterceptorSubject)root).Context.AddFallbackContext(bothContexts);

        // Assert
        Assert.NotEmpty(lifecycleLocksWereFree);
        Assert.All(lifecycleLocksWereFree, Assert.True);
    }
}
