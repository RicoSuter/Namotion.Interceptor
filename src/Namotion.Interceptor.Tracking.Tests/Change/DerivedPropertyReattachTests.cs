using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyReattachTests
{
    [Fact]
    public void WhenDerivedPropertyIsReattachedBeforeItsSource_ThenDependencyIsRestored()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions()
            .WithLifecycle();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var fullNameProperty = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));

        person.DetachSubjectProperty(fullNameProperty);
        person.DetachSubjectProperty(firstNameProperty);
        person.DetachSubjectProperty(lastNameProperty);

        // Act
        person.AttachSubjectProperty(fullNameProperty);
        person.AttachSubjectProperty(firstNameProperty);
        person.AttachSubjectProperty(lastNameProperty);

        changes.Clear();
        person.FirstName = "Jane";

        // Assert
        Assert.Contains(firstNameProperty, fullNameProperty.GetRequiredProperties().ToArray());
        Assert.Contains(lastNameProperty, fullNameProperty.GetRequiredProperties().ToArray());
        Assert.Contains(fullNameProperty, firstNameProperty.GetUsedByProperties().Items.ToArray());
        Assert.Contains(changes, change =>
            change.Property == fullNameProperty &&
            change.GetNewValue<string?>() == "Jane Doe");
    }

    [Fact]
    public async Task WhenSubjectIsMovedWhileADerivedChangeIsBeingPublished_ThenTheLastPublishedValueIsCurrent()
    {
        // Arrange
        var blocker = new ValueBlockingWriteInterceptor(nameof(Person.FullName), "A D");
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.WithService(() => blocker);

        var parent = new Person(context);
        var child = new Person { FirstName = "J", LastName = "D" };
        parent.Mother = child;

        var fullNameProperty = new PropertyReference(child, nameof(Person.FullName));
        var published = new List<string?>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property == fullNameProperty)
                {
                    lock (published)
                    {
                        published.Add(change.GetNewValue<string?>());
                    }
                }
            });

        // Act
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { child.FirstName = "A"; });
        Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(10)));

        parent.Mother = null;
        parent.Father = child;
        child.FirstName = "B";

        blocker.Proceed.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal("B D", child.FullName);
        Assert.Equal("B D", published[^1]);
    }

    [Fact]
    public async Task WhenSourceIsReattachedWhileADependentWaitsToRegisterOnIt_ThenTheDependencyIsKept()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var holder = new UntrackedReferenceHolder(context);
        var leaf = new UntrackedReferenceLeaf();
        holder.First = leaf;
        holder.Watched = leaf;

        var valueProperty = new PropertyReference(leaf, nameof(UntrackedReferenceLeaf.Value));
        var observedProperty = new PropertyReference(holder, nameof(UntrackedReferenceHolder.Observed));
        var valueData = valueProperty.TryGetDerivedPropertyData()!;
        var observedData = observedProperty.TryGetDerivedPropertyData()!;

        // Act: Observed's recalculation waits for its own lock while the leaf is detached and attached again.
        Thread? writerThread = null;
        Task writer;
        Monitor.Enter(observedData);
        var contentionBefore = Monitor.LockContentionCount;
        try
        {
            writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                writerThread = Thread.CurrentThread;
                holder.Trigger = 1;
            });

            Assert.True(SpinWait.SpinUntil(
                () => writerThread is not null &&
                      (writerThread.ThreadState & ThreadState.WaitSleepJoin) != 0 &&
                      Monitor.LockContentionCount > contentionBefore,
                TimeSpan.FromSeconds(10)));

            holder.First = null;
            holder.First = leaf;

            // Data is never removed or replaced, so the registration that follows lands on the leaf's current data.
            Assert.Same(valueData, valueProperty.TryGetDerivedPropertyData());
        }
        finally
        {
            Monitor.Exit(observedData);
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);
        leaf.Value = 5;

        // Assert
        Assert.Contains(valueProperty, observedProperty.GetRequiredProperties().ToArray());
        Assert.Contains(changes, change => change.Property == observedProperty && change.GetNewValue<int>() == 5);
    }

    [Fact]
    public async Task WhenDerivedPropertyIsReattachedBeforeADerivedSourceThatIsRecalculating_ThenItStillFollowsIt()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        // The second write of X is the publish of Nickname's own recalculation.
        var blocker = new ValueBlockingWriteInterceptor(nameof(DerivedSetterPerson.Nickname), "X", occurrence: 2);
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.WithService(() => blocker);

        var person = new DerivedSetterPerson(context) { Nickname = "A" };
        var nicknameProperty = new PropertyReference(person, nameof(DerivedSetterPerson.Nickname));
        var prefixedProperty = new PropertyReference(person, nameof(DerivedSetterPerson.NicknameWithPrefix));

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }
            });

        // Act: the subject is attached again, reader first, while Nickname's recalculation is in flight.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { person.Nickname = "X"; });
        Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(10)));

        person.DetachSubjectProperty(prefixedProperty);
        person.DetachSubjectProperty(nicknameProperty);
        person.AttachSubjectProperty(prefixedProperty);
        person.AttachSubjectProperty(nicknameProperty);

        blocker.Proceed.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        lock (changes)
        {
            changes.Clear();
        }

        person.Nickname = "Y";

        // Assert
        Assert.Contains(nicknameProperty, prefixedProperty.GetRequiredProperties().ToArray());
        lock (changes)
        {
            Assert.Contains(changes, change =>
                change.Property == prefixedProperty &&
                change.GetNewValue<string?>() == "Mr. Y");
        }
    }

    [Fact]
    public async Task WhenPropertyIsDetachedWhileAnAttachWaitsForItsData_ThenTheAttachUsesTheCurrentData()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John", LastName = "Doe" };
        var fullNameProperty = new PropertyReference(person, nameof(Person.FullName));
        var fullNameData = fullNameProperty.TryGetDerivedPropertyData()!;

        // Act: an attach outside the graph lock, as for a property added at runtime, fetches the data
        // and waits for its lock while the property is detached and a dependent's registration fetches its data.
        Thread? attachThread = null;
        Task attach;
        Monitor.Enter(fullNameData);
        var contentionBefore = Monitor.LockContentionCount;
        try
        {
            attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                attachThread = Thread.CurrentThread;
                person.AttachSubjectProperty(fullNameProperty);
            });

            Assert.True(SpinWait.SpinUntil(
                () => attachThread is not null &&
                      (attachThread.ThreadState & ThreadState.WaitSleepJoin) != 0 &&
                      Monitor.LockContentionCount > contentionBefore,
                TimeSpan.FromSeconds(10)));

            person.DetachSubjectProperty(fullNameProperty);
            fullNameProperty.GetDerivedPropertyData();

            // Data is never removed or replaced, so the waiting attach holds the current data.
            Assert.Same(fullNameData, fullNameProperty.TryGetDerivedPropertyData());
        }
        finally
        {
            Monitor.Exit(fullNameData);
        }

        await attach.WaitAsync(TimeSpan.FromSeconds(10));

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);
        person.FirstName = "Jane";

        // Assert
        Assert.NotNull(fullNameProperty.TryGetDerivedPropertyData());
        Assert.Contains(changes, change =>
            change.Property == fullNameProperty &&
            change.GetNewValue<string?>() == "Jane Doe");
    }

    [Fact]
    public async Task WhenSourceIsDetachedWhileItsDependentIsLocked_ThenTheDetachDoesNotWaitAndBothDirectionsAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var holder = new UntrackedReferenceHolder(context);
        var leaf = new UntrackedReferenceLeaf(context);
        holder.First = leaf;
        holder.Watched = leaf;
        holder.Trigger = 1;

        var valueProperty = new PropertyReference(leaf, nameof(UntrackedReferenceLeaf.Value));
        var observedProperty = new PropertyReference(holder, nameof(UntrackedReferenceHolder.Observed));
        var observedData = observedProperty.TryGetDerivedPropertyData()!;

        // Act: the leaf is detached while Observed's lock is held. Only a derived property changes its own links,
        // so the detach completes without it. Observed then drops Value and reads it again.
        Task detach;
        Monitor.Enter(observedData);
        try
        {
            detach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { holder.First = null; });
            Assert.True(SpinWait.SpinUntil(() => detach.IsCompleted, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Monitor.Exit(observedData);
        }

        await detach;
        holder.Trigger = 0;
        holder.Trigger = 1;

        // Assert
        Assert.Contains(valueProperty, observedProperty.GetRequiredProperties().ToArray());
        Assert.Contains(observedProperty, valueProperty.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenSubjectIsDetached_ThenItsTrackingDataIsKept()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(context);
        var child = new Person { FirstName = "John", LastName = "Doe" };
        parent.Mother = child;

        var fullNameProperty = new PropertyReference(child, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(child, nameof(Person.FirstName));
        var fullNameData = fullNameProperty.TryGetDerivedPropertyData();
        var firstNameData = firstNameProperty.TryGetDerivedPropertyData();
        Assert.NotNull(fullNameData);
        Assert.NotNull(firstNameData);

        // Act
        parent.Mother = null;

        // Assert: data is created once and never removed or replaced, so a registration or attach that fetched it
        // before the detach still holds the current data.
        Assert.Same(fullNameData, fullNameProperty.TryGetDerivedPropertyData());
        Assert.Same(firstNameData, firstNameProperty.TryGetDerivedPropertyData());
    }

    [Fact]
    public void WhenDependentRegistersOnADetachedDerivedProperty_ThenARecalculationDoesNotReattachIt()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var car = new Car(context);
        var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
        var pressureProperty = new PropertyReference(car.Tires[0], nameof(Tire.Pressure));
        car.DetachSubjectProperty(averagePressureProperty);

        // A dependent registering on the detached property fetches its data, which stays marked detached.
        averagePressureProperty.GetDerivedPropertyData();

        // Act: a write that took its used-by snapshot before the detach recalculates afterwards.
        DerivedPropertyChangeHandler.RequestRecalculation(averagePressureProperty, 0);

        // Assert
        Assert.DoesNotContain(averagePressureProperty, pressureProperty.GetUsedByProperties().Items.ToArray());
        Assert.Empty(averagePressureProperty.GetRequiredProperties().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectWithDerivedPropertyIsRemovedWhileItsSourceStaysAttached_ThenSubjectCanBeGarbageCollected(bool moveBeforeRemoval)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var garage = new Garage(context);

        // Act
        var weakCar = AddCarSharingATireAndRemoveIt(garage, moveBeforeRemoval);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        var sharedTire = garage.SpareTires[0];
        Assert.False(weakCar.IsAlive);
        Assert.Empty(new PropertyReference(sharedTire, nameof(Tire.Pressure)).GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenSiblingSourceIsReattached_ThenDependentsReachingItThroughFieldsAndTrackedPropertiesFollowIt()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new ReferenceRoot(context);
        var source = new ReferenceSource();
        root.Source = source;
        var reader = new ReferenceReader(root, source);
        root.Readers = [reader];
        source.Value = 1;

        // Act
        root.Source = null;
        root.Source = source;
        source.Value = 2;

        // Assert: only a derived property changes its own links, so the source's detach kept the field reader's link.
        Assert.Equal(2, GetLastKnownValue(reader, nameof(ReferenceReader.ViaField)));
        Assert.Equal(2, GetLastKnownValue(reader, nameof(ReferenceReader.ViaTrackedPath)));
    }

    [Fact]
    public void WhenParentReachedThroughAFieldIsReattached_ThenTheDependentFollowsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new ReferenceRoot(context);
        var source = new ReferenceSource();
        root.Source = source;
        var reader = new ReferenceReader(root, source);
        source.Readers = [reader];
        source.Value = 1;

        // Act
        root.Source = null;
        root.Source = source;
        source.Value = 2;

        // Assert
        Assert.Equal(2, GetLastKnownValue(reader, nameof(ReferenceReader.ViaField)));
    }

    private static object? GetLastKnownValue(IInterceptorSubject subject, string propertyName)
    {
        return new PropertyReference(subject, propertyName).TryGetDerivedPropertyData()?.LastKnownValue;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddCarSharingATireAndRemoveIt(Garage garage, bool moveBeforeRemoval)
    {
        // The shared tire joins the garage first, so its inherited context is the garage's,
        // and the derived property is the only link from the tire back to the car.
        var car = new Car();
        garage.SpareTires = [car.Tires[0]];
        garage.Cars = [car];

        if (moveBeforeRemoval)
        {
            garage.Cars = [];
            garage.CarsByName = new Dictionary<string, Car> { ["moved"] = car };
            garage.CarsByName = new Dictionary<string, Car>();
        }
        else
        {
            garage.Cars = [];
        }

        return new WeakReference(car);
    }

    [Fact]
    public async Task WhenSubjectIsAttachedAgainWhileItsDerivedPropertyIsRecalculated_ThenChangeIsPublishedFromTheValueOfThatAttach()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(30);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        using var recalculationIsEvaluating = new ManualResetEventSlim();
        using var subjectIsAttachedAgain = new ManualResetEventSlim();
        var pauseNextEvaluation = 0;
        machine.Worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            if (Interlocked.Exchange(ref pauseNextEvaluation, 0) == 1)
            {
                recalculationIsEvaluating.Set();
                subjectIsAttachedAgain.Wait(timeout);
            }

            return anchor;
        };
        root.Machine = machine;
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        Interlocked.Exchange(ref pauseNextEvaluation, 1);

        // Act: the attach evaluates the value 1, and the write of 2 is left to the recalculation in flight.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = 1; });
        var isEvaluating = recalculationIsEvaluating.Wait(timeout);
        root.Machine = null;
        root.Machine = machine;
        root.Anchor = 2;
        subjectIsAttachedAgain.Set();
        await writer.WaitAsync(timeout);

        // Assert
        Assert.True(isEvaluating);
        var change = Assert.Single(changes, c => c.Property.Name == nameof(EvaluatorWorker.Result));
        Assert.Equal(1, change.GetOldValue<int>());
        Assert.Equal(2, change.GetNewValue<int>());
    }
}
