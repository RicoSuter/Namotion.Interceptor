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
    public async Task WhenSourceIsMovedWhileADependentWaitsToRegisterOnIt_ThenTheDependencyIsKept()
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

        // Act: Observed's recalculation fetches the leaf's data, then waits for its lock while the leaf moves.
        Thread? writerThread = null;
        Task writer;
        Monitor.Enter(valueData);
        try
        {
            writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                writerThread = Thread.CurrentThread;
                holder.Trigger = 1;
            });

            Assert.True(SpinWait.SpinUntil(
                () => writerThread is not null && (writerThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)));

            holder.First = null;
            holder.Second = leaf;
        }
        finally
        {
            Monitor.Exit(valueData);
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
    public void WhenSubjectIsDetached_ThenItsTrackingDataIsRemoved()
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
        Assert.NotNull(fullNameProperty.TryGetDerivedPropertyData());
        Assert.NotNull(firstNameProperty.TryGetDerivedPropertyData());

        // Act
        parent.Mother = null;

        // Assert
        Assert.Null(fullNameProperty.TryGetDerivedPropertyData());
        Assert.Null(firstNameProperty.TryGetDerivedPropertyData());
    }

    [Fact]
    public void WhenDetachedDerivedPropertyDataIsRecreatedByADependent_ThenARecalculationDoesNotReattachIt()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var car = new Car(context);
        var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
        var pressureProperty = new PropertyReference(car.Tires[0], nameof(Tire.Pressure));
        car.DetachSubjectProperty(averagePressureProperty);

        // A dependent registering on the detached property recreates its data, attached but not derived.
        averagePressureProperty.GetDerivedPropertyData();

        // Act: a write that took its used-by snapshot before the detach recalculates afterwards.
        DerivedPropertyChangeHandler.RecalculateDerivedProperty(ref averagePressureProperty, 0, 0);

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
}
