using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

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
        AssertEvaluatedAgain(machine.Device);
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
        AssertEvaluatedAgain(machine.Device);
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
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
        AssertEvaluatedAgain(first.Device);
        AssertEvaluatedAgain(second.Device);
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
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;
        var changesOfAttach = changes.ToArray();
        machine.Configuration.Value = 5;

        // Assert: the evaluation that succeeds first gives the property its first value, which is not a change.
        Assert.DoesNotContain(changesOfAttach, change => change.Property.Subject == machine.Worker);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 5);
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
            // The worker is attached, the configuration has no context yet, so its write is not tracked.
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
            .Select(change => change.GetNewValue<int>());
        Assert.Equal([100, 105], values);
    }

    [Fact]
    public void WhenValueChangesWithoutNewDependency_ThenAttachPublishesNoChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker =>
        {
            // Reads the same tracked property in both evaluations: the parent of the configuration is not tracked.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? 1 : 0;
        };
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert: what the getter reads besides tracked properties is not followed, see docs/tracking.md.
        Assert.Equal(1, machine.Worker.Result);
        Assert.DoesNotContain(changes, change => change.Property.Name == nameof(EvaluatorWorker.Result));
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
            // Reads the same tracked properties in both evaluations: the parent of the configuration is not tracked.
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
        machine.Worker.ObjectEvaluator = _ => new ValueThatCannotBeCompared();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        var changes = SubscribeToChanges(context);

        // Act
        var exception = Record.Exception(() => root.Machine = machine);

        // Assert
        Assert.Null(exception);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 1);
    }

    [Fact]
    public void WhenGetterAlsoReadsTheWrittenProperty_ThenValueBuiltAnewIsPublishedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();

        // Gains the dependency on the value when the attach ends, and is recalculated by the write of the machine.
        machine.Worker.ObjectEvaluator = _ => new List<int> { root.Machine?.Configuration.Value ?? 0 };
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
    public async Task WhenSubtreeIsAttachedAgainOnAnotherThreadBeforeTheWriteReturns_ThenTheLaterAttachPublishesTheChangedDerivedValue()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(30);
        var timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = EvaluatorWorker.ReadConfigurationOnceAttached;
        using var secondAttachIsDone = new ManualResetEventSlim();
        using var firstWriteHasReturned = new ManualResetEventSlim();
        var secondWriter = Task.CompletedTask;
        var machineWrites = 0;
        context.WithService(() => new AfterWriteInterceptor(property =>
        {
            var write = property.Name == nameof(EvaluatorRoot.Machine) ? Interlocked.Increment(ref machineWrites) : 0;
            if (write == 1)
            {
                // The first write has attached the machine and has not published its derived value yet.
                secondWriter = Task.Run(() =>
                {
                    root.Machine = null;
                    using (SubjectChangeContext.WithChangedTimestamp(timestamp))
                    {
                        root.Machine = machine;
                    }
                });
                Assert.True(secondAttachIsDone.Wait(timeout));
            }
            else if (write == 3)
            {
                // The second attach is done as well: the first write returns before this one publishes.
                secondAttachIsDone.Set();
                Assert.True(firstWriteHasReturned.Wait(timeout));
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;
        firstWriteHasReturned.Set();
        await secondWriter.WaitAsync(timeout);

        // Assert
        var change = Assert.Single(changes, c => c.Property.Name == nameof(EvaluatorWorker.Result));
        Assert.Equal(timestamp, change.ChangedTimestamp);
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
    public void WhenDerivedPropertyBuildsItsValueAnew_ThenAttachPublishesNoChangeOfAnUnchangedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        var changes = SubscribeToChanges(context);

        // Act: the first machine is evaluated again, because the second one is attached after it.
        root.Machines = [machine, new SiblingReadingMachine()];

        // Assert: the list and the immutable array gained no dependency, and the array gained one but has the same items.
        AssertEvaluatedAgain(machine.Device);
        Assert.DoesNotContain(changes, change => change.Property.Name == nameof(SiblingReadingMachine.InstalledStateList));
        Assert.DoesNotContain(changes, change => change.Property.Name == nameof(SiblingReadingMachine.InstalledStateValues));
        Assert.DoesNotContain(changes, change => change.Property.Name == nameof(SiblingReadingDevice.InstalledStates));
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
        // The worker is evaluated again when the attach ends, and its changed value is published after it.
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

    private sealed class ValueThatCannotBeCompared
    {
#pragma warning disable S3877 // An Equals that throws is what the test is about.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException();
        }
#pragma warning restore S3877

        public override int GetHashCode()
        {
            return 0;
        }
    }

    [RunsAfter(typeof(DerivedPropertyChangeHandler))]
    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class AfterWriteInterceptor(Action<PropertyReference> callback) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            callback(context.Property);
        }
    }

    private sealed class CallbackLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            callback(change);
        }
    }

    [RunsBefore(typeof(ContextInheritanceHandler))]
    private sealed class BeforeContextInheritanceLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            callback(change);
        }
    }

    private static List<SubjectPropertyChange> SubscribeToChanges(IInterceptorSubjectContext context)
    {
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        return changes;
    }

    // The tests of a sibling attached later mean nothing if the device is attached last: it is then evaluated once.
    private static void AssertEvaluatedAgain(SiblingReadingDevice device)
    {
        Assert.True(device.EvaluationCount >= 2, $"The device was evaluated {device.EvaluationCount} time(s).");
    }

    private static void AssertSingleChange<T>(List<SubjectPropertyChange> changes, IInterceptorSubject subject, string propertyName, T newValue)
    {
        var change = Assert.Single(changes, c => c.Property.Subject == subject && c.Property.Name == propertyName);
        Assert.Equal(newValue, change.GetNewValue<T>());
    }
}
