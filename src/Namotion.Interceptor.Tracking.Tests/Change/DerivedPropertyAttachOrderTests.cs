using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Change.DerivedPropertyTestHelpers;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyAttachOrderTests
{
    [Fact]
    public void WhenSubtreeIsAssigned_ThenDerivedPropertyFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenRootIsAttachedToContext_ThenDerivedPropertyFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var machine = new SiblingReadingMachine();
        var root = new SiblingReadingRoot { Machine = machine };
        ((IInterceptorSubject)root).Context.AddFallbackContext(context);
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenDerivedPropertyIsAddedAtRuntimeDuringTheAttach_ThenItFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        const string propertyName = "IsInstalledAtRuntime";
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents().WithRegistry();
        context.WithService(() => new AfterRegistryLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingDevice device })
            {
                device.TryGetRegisteredSubject()!.AddDerivedProperty<bool>(propertyName, subject =>
                    ((SiblingReadingDevice)subject).TryGetFirstParent<SiblingReadingMachine>()?.Configuration.Installed ?? false);
            }
        }));
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, propertyName, true);
    }

    [Fact]
    public void WhenCollectionIsAssigned_ThenDerivedPropertyOfEveryItemFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var first = new SiblingReadingMachine();
        var second = new SiblingReadingMachine();
        root.Machines = [first, second];
        var changes = SubscribeToChanges(context);

        // Act
        first.Configuration.Installed = true;
        second.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, first.Device, nameof(SiblingReadingDevice.IsInstalled), true);
        AssertSingleChange(changes, second.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenLaterSiblingIsWrittenDuringTheAttach_ThenDerivedPropertyPublishesTheChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingConfiguration configuration })
            {
                configuration.Installed = true;
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenLifecycleHandlerThrowsDuringTheAttach_ThenDerivedPropertyAttachedBeforeFollowsLaterSibling()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        var throwingMachine = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == throwingMachine)
            {
                throw new InvalidOperationException();
            }
        }));
        Assert.Throws<InvalidOperationException>(() => root.Machines = [machine, throwingMachine]);
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenFailedFirstEvaluationIsRecalculatedDuringTheAttach_ThenValueChangedAfterItIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker => root.Anchor == 0
            ? throw new InvalidOperationException()
            : 100 + worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
        context.WithService(() => new BeforeContextInheritanceLifecycleHandler(change =>
        {
            // The worker is attached, the configuration has no context yet.
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                root.Anchor = 1;
                new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).RecalculateDerivedProperty();
                machine.Configuration.Value = 5;
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        var values = changes
            .Where(change => change.Property == new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)))
            .Select(change => change.GetNewValue<int>())
            .ToArray();
        Assert.NotEmpty(values);
        Assert.Equal(105, values[^1]);
        Assert.Equal(105, machine.Worker.Result);
    }

    [Fact]
    public void WhenGetterAlsoReadsTheWrittenProperty_ThenValueBuiltAnewIsPublishedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();

        // A new list on every evaluation, so every recalculation publishes a change.
        machine.Worker.ObjectEvaluator = _ => new List<int> { root.Machine?.Configuration.Value ?? 0 };
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        Assert.Single(changes, change => change.Property.Name == nameof(EvaluatorWorker.ObjectResult));
    }

    [Fact]
    public void WhenArrayHoldsAnArrayOfAnotherRank_ThenAttachCompletesAndDerivedPropertyFollowsTheSibling()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        var change = Assert.Single(changes, c => c.Property.Name == nameof(SiblingReadingDevice.InstalledGrids));
        Assert.True(((bool[,])change.GetNewValue<object[]>()[0])[0, 0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubtreeWithChangedDerivedValueIsDetached_ThenItIsNotKeptAlive(bool attachToContext)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);

        // Act
        var subtree = AttachAndDetachSubtree(context, root, attachToContext);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        Assert.False(subtree.IsAlive);
        GC.KeepAlive(root);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDetachSubtree(IInterceptorSubjectContext context, EvaluatorRoot root, bool attachToContext)
    {
        // The worker's value depends on whether the configuration has a parent, which changes during the attach.
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        if (attachToContext)
        {
            var otherRoot = new EvaluatorRoot { Machine = machine };
            ((IInterceptorSubject)otherRoot).Context.AddFallbackContext(context);
            ((IInterceptorSubject)otherRoot).Context.RemoveFallbackContext(context);
            return new WeakReference(otherRoot);
        }

        root.Machine = machine;
        root.Machine = null;
        return new WeakReference(machine);
    }

    [Fact]
    public void WhenDerivedPropertyIsRecalculatedDuringTheAttach_ThenItFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();

        // Reads only a value at attach, and the later sibling once the anchor is set.
        machine.Worker.Evaluator = worker => root.Anchor == 0
            ? 0
            : 100 + worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
        context.WithService(() => new BeforeContextInheritanceLifecycleHandler(change =>
        {
            // The worker is attached, the configuration has no context yet.
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                root.Anchor = 1;
            }
        }));
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Value = 7;

        // Assert
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 107);
    }

    [Fact]
    public void WhenDerivedPropertyIsAddedAtRuntimeByAHandlerOfTheLastSibling_ThenItFollowsThatSibling()
    {
        // Arrange
        const string propertyName = "ConfigurationValueAtRuntime";
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents().WithRegistry();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        context.WithService(() => new BeforeContextInheritanceLifecycleHandler(change =>
        {
            // The configuration is the last subject of the operation and has no context yet.
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                machine.Worker.TryGetRegisteredSubject()!.AddDerivedProperty<int>(propertyName, subject =>
                    ((EvaluatorWorker)subject).TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value);
            }
        }));
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Value = 5;

        // Assert
        AssertSingleChange(changes, machine.Worker, propertyName, 5);
    }

    [Fact]
    public void WhenStructuralWriteFails_ThenChangedDerivedValueIsPublishedAndTheExceptionOfTheWriteWins()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        var failingMachine = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingConfiguration configuration })
            {
                configuration.Installed = true;
            }

            if (change.IsContextAttach && change.Subject == failingMachine)
            {
                throw new NotSupportedException();
            }
        }));
        var changes = SubscribeToChanges(context);
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == machine.Device && change.Property.Name == nameof(SiblingReadingDevice.IsInstalled))
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        var exception = Record.Exception(() => root.Machines = [machine, failingMachine]);

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenContextAttachFails_ThenItsExceptionWinsOverAnObserverThatThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var machine = new SiblingReadingMachine();
        var failingMachine = new SiblingReadingMachine();
        var root = new SiblingReadingRoot { Machines = [machine, failingMachine] };
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingConfiguration configuration })
            {
                configuration.Installed = true;
            }

            if (change.IsContextAttach && change.Subject == failingMachine)
            {
                throw new NotSupportedException();
            }
        }));
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == nameof(SiblingReadingDevice.IsInstalled))
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).Context.AddFallbackContext(context));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    [Fact]
    public void WhenObserverThrowsOnChangedDerivedValue_ThenRemainingDerivedPropertiesStillPublish()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var first = new SiblingReadingMachine();
        var second = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingConfiguration configuration })
            {
                configuration.Installed = true;
            }
        }));

        // Subscribed first, because an observer that throws keeps later observers from seeing that change.
        var changes = SubscribeToChanges(context);
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == first.Device)
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        var exception = Record.Exception(() => root.Machines = [first, second]);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(changes, change => change.Property.Subject == root && change.Property.Name == nameof(SiblingReadingRoot.Machines));
        AssertSingleChange(changes, second.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public async Task WhenSubtreeIsAttachedAgainOnAnotherThreadBeforeTheWriteDrains_ThenItsQueuedRequestIsDroppedAndTheChangeCarriesTheLaterAttachTimestamp()
    {
        // Arrange: each attach of the configuration writes its value under the lifecycle lock, which queues a request
        // for the worker's result. The first write's queued work runs after the second attach and before that
        // attach's own queued work.
        var timeout = TestTimeouts.Default;
        var firstTimestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var secondTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => machine.Configuration.Value;
        var configurationAttaches = 0;
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                machine.Configuration.Value = ++configurationAttaches;
            }
        }));
        using var secondAttachIsDone = new ManualResetEventSlim();
        using var firstWriteHasReturned = new ManualResetEventSlim();
        var secondWriter = Task.CompletedTask;
        var machineWrites = 0;
        var hasSecondAttachCompleted = false;
        var hasFirstWriteReturned = false;
        context.WithService(() => new AfterWriteInterceptor(property =>
        {
            var write = property.Name == nameof(EvaluatorRoot.Machine) ? Interlocked.Increment(ref machineWrites) : 0;
            if (write == 1)
            {
                // The first write released the lifecycle lock and has not run its queued work yet.
                secondWriter = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    root.Machine = null;
                    using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
                    {
                        root.Machine = machine;
                    }
                });
                hasSecondAttachCompleted = secondAttachIsDone.Wait(timeout);
            }
            else if (write == 3)
            {
                secondAttachIsDone.Set();
                hasFirstWriteReturned = firstWriteHasReturned.Wait(timeout);
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            root.Machine = machine;
        }

        firstWriteHasReturned.Set();
        await secondWriter.WaitAsync(timeout);

        // Assert
        Assert.True(hasSecondAttachCompleted);
        Assert.True(hasFirstWriteReturned);
        var change = Assert.Single(changes, c => c.Property.Subject == machine.Worker && c.Property.Name == nameof(EvaluatorWorker.Result));
        Assert.Equal((1, 2), (change.GetOldValue<int>(), change.GetNewValue<int>()));
        Assert.Equal(secondTimestamp, change.ChangedTimestamp);
    }

    [Fact]
    public async Task WhenObserverWritesGraphWhileAttachPublishesChangedDerivedValue_ThenBothWritesComplete()
    {
        // Arrange: the worker attaches before the configuration and reads its value. A handler writes that value
        // while the configuration attaches, which requests the worker's second evaluation. The second evaluation
        // waits until an observer of another thread holds the dispatch lock of the observable and writes the graph,
        // which takes the lifecycle lock; the evaluation then publishes, which takes the dispatch lock.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var source = new EvaluatorConfiguration(context);
        var machine = new EvaluatorMachine();
        var secondEvaluationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerWritesGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluationCount = 0;
        machine.Worker.Evaluator = worker =>
        {
            var result = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
            if (++evaluationCount == 2)
            {
                secondEvaluationReached.SetResult();
                observerWritesGraph.Task.Wait(TestTimeouts.Default);
            }

            return result;
        };
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                machine.Configuration.Value = 1;
            }
        }));

        var publishedResults = new List<int>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, source))
            {
                observerWritesGraph.SetResult();
                root.Spare = new EvaluatorConfiguration();
            }
            else if (ReferenceEquals(change.Property.Subject, machine.Worker))
            {
                publishedResults.Add(change.GetNewValue<int>());
            }
        });

        // Act
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Machine = machine; });
        await secondEvaluationReached.Task.WaitAsync(TestTimeouts.Default);
        var valueWrite = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { source.Value = 1; });
        await Task.WhenAll(valueWrite, attach).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal([1], publishedResults);
        Assert.NotNull(root.Spare);
    }

    private static void AssertSingleChange<T>(List<SubjectPropertyChange> changes, IInterceptorSubject subject, string propertyName, T newValue)
    {
        var change = Assert.Single(changes, c => c.Property.Subject == subject && c.Property.Name == propertyName);
        Assert.Equal(newValue, change.GetNewValue<T>());
    }

    // Runs its callback after the lifecycle interceptor reconciled the write and released its lock.
    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class AfterWriteInterceptor(Action<PropertyReference> callback) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            callback(context.Property);
        }
    }
}
