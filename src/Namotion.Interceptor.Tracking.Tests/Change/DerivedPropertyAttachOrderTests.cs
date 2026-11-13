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
    public void WhenFirstEvaluationThrows_ThenDerivedPropertyFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker =>
        {
            // Throws in the first evaluation: the configuration has no parent before it is attached.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return configuration.TryGetFirstParent<EvaluatorMachine>() is not null
                ? configuration.Value
                : throw new InvalidOperationException();
        };
        machine.Configuration.Value = 3;
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;
        var changesOfAttach = changes.ToArray();
        changes.Clear();
        machine.Configuration.Value = 5;

        // Assert: the evaluation that succeeds first after the throw is published.
        AssertSingleChange([.. changesOfAttach], machine.Worker, nameof(EvaluatorWorker.Result), 3);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 5);
    }

    [Fact]
    public void WhenGetterReadsParentOfLaterSibling_ThenAttachPublishesTheChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker =>
        {
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? 1 : 0;
        };
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        Assert.Equal(1, machine.Worker.Result);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 1);
    }

    [Fact]
    public void WhenChangedInstanceIsNotPublishedByTheAttach_ThenChangeBackToTheFirstInstanceIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var first = new object();
        var second = new object();
        machine.Worker.ObjectEvaluator = worker =>
        {
            var anchor = root.Anchor;
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return anchor == 0 && configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? second : first;
        };
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        root.Anchor = 1;

        // Assert
        var change = Assert.Single(changes, c => c.Property.Name == nameof(EvaluatorWorker.ObjectResult));
        Assert.Same(second, change.GetOldValue<object?>());
        Assert.Same(first, change.GetNewValue<object?>());
    }

    [Fact]
    public void WhenComparisonOfTheValuesThrows_ThenAttachCompletesAndOtherDerivedPropertiesFollowTheSibling()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var comparisons = 0;

        // Recalculated when the configuration is attached, so that the values are compared.
        machine.Worker.ObjectEvaluator = worker => new ValueThatCannotBeCompared(
            EvaluatorWorker.ReadConfigurationOnceAttached(worker), () => comparisons++);
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        var changes = SubscribeToChanges(context);

        // Act
        var exception = Record.Exception(() => root.Machine = machine);

        // Assert: the exception of the comparison propagates from the write after the queued work ran.
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Same(machine, root.Machine);
        Assert.True(comparisons > 0);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 1);
    }

    [Fact]
    public void WhenGetterReadsTheSamePropertiesInAnotherOrder_ThenValueBuiltAnewIsPublishedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.ObjectEvaluator = worker =>
        {
            // The parent of the configuration switches the order of the two reads.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            if (configuration.TryGetFirstParent<EvaluatorMachine>() is null)
            {
                var anchor = root.Anchor;
                return new List<object?> { anchor, root.Data };
            }

            var data = root.Data;
            return new List<object?> { root.Anchor, data };
        };
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        Assert.Single(changes, change => change.Property.Name == nameof(EvaluatorWorker.ObjectResult));
    }

    [Fact]
    public void WhenSubtreeIsAssignedWithinAWriteThatAttachesNoSubject_ThenChangedDerivedValueCarriesTheTimestampOfTheAssignment()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        context.WithService(() => new AfterWriteInterceptor(property =>
        {
            if (property.Name == nameof(EvaluatorRoot.Data))
            {
                using (SubjectChangeContext.WithChangedTimestamp(timestamp))
                {
                    root.Machine = machine;
                }
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Data = [1];

        // Assert
        var change = Assert.Single(changes, c => c.Property.Name == nameof(EvaluatorWorker.Result));
        Assert.Equal(timestamp, change.ChangedTimestamp);
    }

    [Fact]
    public async Task WhenSubtreeIsAttachedAgainOnAnotherThreadBeforeTheWriteReturns_ThenTheChangedDerivedValueIsPublishedOnce()
    {
        // Arrange
        var timeout = TestTimeouts.Default;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
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
                // The first write has attached the machine and has not published its derived value yet.
                secondWriter = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    root.Machine = null;
                    root.Machine = machine;
                });
                hasSecondAttachCompleted = secondAttachIsDone.Wait(timeout);
            }
            else if (write == 3)
            {
                // The second attach is done as well: the first write returns before this one publishes.
                secondAttachIsDone.Set();
                hasFirstWriteReturned = firstWriteHasReturned.Wait(timeout);
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;
        firstWriteHasReturned.Set();
        await secondWriter.WaitAsync(timeout);

        // Assert: the work the first write queued runs first, after the second attach, and publishes the change of
        // that attachment once.
        Assert.True(hasSecondAttachCompleted);
        Assert.True(hasFirstWriteReturned);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 1);
        Assert.Equal(1, machine.Worker.Result);
    }

    [Fact]
    public void WhenValueTypeThatEnumeratesSubjectsIsAssigned_ThenItIsAnnouncedBeforeItsChangedDerivedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        var changes = SubscribeToChanges(context);

        // Act
        root.MachineSet = EvaluatorMachineSet.Create(machine);

        // Assert
        Assert.Equal(2, changes.Count);
        Assert.Equal(new PropertyReference(root, nameof(EvaluatorRoot.MachineSet)), changes[0].Property);
        Assert.Equal(new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)), changes[1].Property);
        Assert.Equal(changes[0].ChangedTimestamp, changes[1].ChangedTimestamp);
    }

    [Fact]
    public void WhenSubtreeIsAssigned_ThenItIsAnnouncedBeforeItsChangedDerivedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        Assert.Equal(2, changes.Count);
        Assert.Equal(new PropertyReference(root, nameof(EvaluatorRoot.Machine)), changes[0].Property);
        Assert.Equal(new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)), changes[1].Property);
        Assert.Equal(1, changes[1].GetNewValue<int>());
        Assert.Equal(changes[0].ChangedTimestamp, changes[1].ChangedTimestamp);
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

        var lifecycleLockWasFree = RecordWhetherLifecycleLocksAreFree(context, machine.Worker, incomingRoot, interceptor);

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

        var lifecycleLocksWereFree = RecordWhetherLifecycleLocksAreFree(
            firstContext, machine.Worker, root, firstContext.GetService<LifecycleInterceptor>(), secondContext.GetService<LifecycleInterceptor>());

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

        var lifecycleLocksWereFree = RecordWhetherLifecycleLocksAreFree(
            firstContext, machine.Worker, root, firstContext.GetService<LifecycleInterceptor>(), secondContext.GetService<LifecycleInterceptor>());

        // Act
        ((IInterceptorSubject)root).Context.AddFallbackContext(bothContexts);

        // Assert
        Assert.NotEmpty(lifecycleLocksWereFree);
        Assert.All(lifecycleLocksWereFree, Assert.True);
    }

    private static void AssertSingleChange<T>(List<SubjectPropertyChange> changes, IInterceptorSubject subject, string propertyName, T newValue)
    {
        var change = Assert.Single(changes, c => c.Property.Subject == subject && c.Property.Name == propertyName);
        Assert.Equal(newValue, change.GetNewValue<T>());
    }

    /// <summary>
    /// Records, for every published change of the worker's result, whether another thread gets the lifecycle locks,
    /// which it does only if the publishing thread holds none of them.
    /// </summary>
    private static List<bool> RecordWhetherLifecycleLocksAreFree(
        IInterceptorSubjectContext context, EvaluatorWorker worker, IInterceptorSubject attachedSubject, params LifecycleInterceptor[] interceptors)
    {
        var lifecycleLocksWereFree = new List<bool>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, worker) && change.Property.Name == nameof(EvaluatorWorker.Result))
            {
                var otherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    foreach (var interceptor in interceptors)
                    {
                        interceptor.TryRunWhileAttached(attachedSubject, () => { });
                    }
                });
                lifecycleLocksWereFree.Add(otherThread.Wait(TestTimeouts.Default));
            }
        });

        return lifecycleLocksWereFree;
    }

    private sealed class ValueThatCannotBeCompared(int value, Action onCompared)
    {
        public int Value { get; } = value;

#pragma warning disable S3877 // An Equals that throws is what the test is about.
        public override bool Equals(object? obj)
        {
            onCompared();
            throw new InvalidOperationException();
        }
#pragma warning restore S3877

        public override int GetHashCode()
        {
            return 0;
        }
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
