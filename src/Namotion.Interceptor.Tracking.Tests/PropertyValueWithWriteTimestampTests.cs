using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class PropertyValueWithWriteTimestampTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondTimestamp = FirstTimestamp.AddMinutes(1);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A value read takes the subject's SyncRoot only for the read itself, and a timestamp read takes no
    /// lock, so a write can commit between the two. Parking the paired read after its value read and
    /// committing a write there shows the difference: the paired read holds SyncRoot across both reads,
    /// so the write waits and the pair stays (1, first), whereas two separate reads straddle the write.
    /// </summary>
    [Fact]
    public async Task WhenWriteCommitsBetweenValueAndTimestampRead_ThenPairedReadStillComesFromOneWrite()
    {
        // Arrange
        var parking = new ParkingReadInterceptor(nameof(TimestampedCounter.Value));
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => parking, _ => false);

        var subject = new TimestampedCounter(context);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            subject.Value = 1;
        }

        // Act
        var separateValue = subject.Value;

        parking.Armed = true;
        object? pairedValue = null;
        var pairedMetadata = default(PropertyValueMetadata);
        var reader = new Thread(() => pairedValue = property.GetValue(out pairedMetadata))
        {
            IsBackground = true,
            Name = "reader"
        };
        reader.Start();
        Assert.True(parking.ValueRead.Wait(WaitBudget), "The paired read did not reach its parked value read.");

        var writer = new Thread(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(SecondTimestamp))
            {
                subject.Value = 2;
            }
        })
        {
            IsBackground = true,
            Name = "writer"
        };
        writer.Start();

        // The writer either blocks on SyncRoot held by the paired read, which is the behavior under
        // test, or has already committed past it, which the assertion below then reports as the wrong pair.
        await AsyncTestHelpers.WaitUntilAsync(
            () => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0 || !writer.IsAlive,
            message: "The writer neither blocked on the subject's SyncRoot nor completed.");

        parking.Release.Set();
        Assert.True(reader.Join(WaitBudget), "The paired read did not complete after it was released.");
        Assert.True(writer.Join(WaitBudget), "The writer did not complete after the paired read finished.");

        var separateTimestamp = property.TryGetWriteTimestamp();

        // Assert
        Assert.Equal(1, separateValue);
        Assert.Equal(SecondTimestamp, separateTimestamp);
        Assert.Equal(1, pairedValue);
        Assert.Equal(FirstTimestamp, pairedMetadata.WriteTimestamp);
    }

    [Fact]
    public void WhenStoredPropertyIsReadWithMetadata_ThenGetterRunsUnderSyncRoot()
    {
        // Arrange
        var observer = new SyncRootObservingReadInterceptor();
        var context = InterceptorSubjectContext.Create().WithService(() => observer, _ => false);
        var subject = new TimestampedCounter(context);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));

        // Act
        _ = subject.Value;
        var syncRootHeldForPlainRead = observer.SyncRootHeld;

        _ = property.GetValue(out _);
        var syncRootHeldForPairedRead = observer.SyncRootHeld;

        // Assert
        Assert.False(syncRootHeldForPlainRead);
        Assert.True(syncRootHeldForPairedRead);
    }

    /// <summary>
    /// A derived getter recomputes from its dependencies as soon as they are stored, while the derived
    /// property's timestamp is stamped by the recalculation that follows. Parking the trigger write
    /// between the two leaves the getter ahead of the timestamp; the paired read returns what the last
    /// recalculation committed instead, which is the pair its change notification carries.
    /// </summary>
    [Fact]
    public async Task WhenDerivedPropertyIsReadWhileItsRecalculationIsPending_ThenValueAndTimestampAreTheLastCommitted()
    {
        // Arrange
        var parking = new ParkingWriteInterceptor(nameof(Person.FirstName));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(parking);

        var person = new Person(context);
        var fullName = person.GetPropertyReference(nameof(Person.FullName));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            person.FirstName = "John";
        }

        parking.Armed = true;
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(SecondTimestamp))
            {
                person.FirstName = "Jane";
            }
        }, "writer");

        Assert.True(parking.Committed.Wait(WaitBudget), "The writer did not reach the parked commit.");

        // Act
        var separateValue = person.FullName;
        var separateTimestamp = fullName.TryGetWriteTimestamp();
        var pairedValue = fullName.GetValue(out var pairedMetadata);

        parking.Release.Set();
        await writer;

        var settledValue = fullName.GetValue(out var settledMetadata);

        // Assert
        Assert.Equal("Jane", separateValue);
        Assert.Equal(FirstTimestamp, separateTimestamp);
        Assert.Equal("John", pairedValue);
        Assert.Equal(FirstTimestamp, pairedMetadata.WriteTimestamp);
        Assert.Equal("Jane", settledValue);
        Assert.Equal(SecondTimestamp, settledMetadata.WriteTimestamp);
    }

    /// <summary>
    /// The terminal of a derived-with-setter write stamps the property's write state before the recalculation
    /// that follows commits the value that write produces, so the paired read must not take its timestamp from there.
    /// </summary>
    [Fact]
    public async Task WhenDerivedPropertyWithSetterIsReadWhileItsRecalculationIsPending_ThenValueAndTimestampAreTheLastCommitted()
    {
        // Arrange
        var parking = new ParkingWriteInterceptor(nameof(DerivedSetterPerson.Nickname));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(parking);

        var person = new DerivedSetterPerson(context);
        var nickname = person.GetPropertyReference(nameof(DerivedSetterPerson.Nickname));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            person.Nickname = "John";
        }

        parking.Armed = true;
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(SecondTimestamp))
            {
                person.Nickname = "Jane";
            }
        }, "writer");

        Assert.True(parking.Committed.Wait(WaitBudget), "The writer did not reach the parked commit.");

        // Act
        var separateTimestamp = nickname.TryGetWriteTimestamp();
        var pairedValue = nickname.GetValue(out var pairedMetadata);

        parking.Release.Set();
        await writer;

        var settledValue = nickname.GetValue(out var settledMetadata);

        // Assert
        Assert.Equal(SecondTimestamp, separateTimestamp);
        Assert.Equal("John", pairedValue);
        Assert.Equal(FirstTimestamp, pairedMetadata.WriteTimestamp);
        Assert.Equal("Jane", settledValue);
        Assert.Equal(SecondTimestamp, settledMetadata.WriteTimestamp);
    }

    [Fact]
    public void WhenDerivedGetterThrewAtAttach_ThenGetterIsInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // The denominator is zero when the subject attaches, so the getter throws there and no recalculation follows.
        var divider = new Divider(context);
        divider.Numerator = 10;
        divider.Denominator = 2;
        var quotient = divider.GetPropertyReference(nameof(Divider.Quotient));

        // Act
        var value = quotient.GetValue(out var metadata);

        // Assert
        Assert.Equal(5, value);
        Assert.Null(metadata.WriteTimestamp);
    }

    [Fact]
    public void WhenDerivedPropertyIsReadWithoutDerivedPropertyChangeDetection_ThenGetterIsInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context) { FirstName = "John" };
        var fullName = person.GetPropertyReference(nameof(Person.FullName));

        // Act
        var value = fullName.GetValue(out var metadata);

        // Assert
        Assert.Equal("John", value);
        Assert.Null(metadata.WriteTimestamp);
    }

    private sealed class SyncRootObservingReadInterceptor : IReadInterceptor
    {
        public bool SyncRootHeld { get; private set; }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            SyncRootHeld = Monitor.IsEntered(context.Property.Subject.SyncRoot);
            return next(ref context);
        }
    }

    // Once armed, parks the reader after the read terminal has returned the value and released its lock.
    private sealed class ParkingReadInterceptor(string propertyName) : IReadInterceptor
    {
        public volatile bool Armed;

        public ManualResetEventSlim ValueRead { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);

            if (Armed && context.Property.Name == propertyName)
            {
                ValueRead.Set();
                if (!Release.Wait(WaitBudget))
                {
                    throw new TimeoutException("The test did not release the parked read.");
                }
            }

            return value;
        }
    }

    // Once armed, parks the writer after the terminal has committed and before the derived handler recalculates.
    [RunsAfter(typeof(DerivedPropertyChangeHandler))]
    private sealed class ParkingWriteInterceptor(string propertyName) : IWriteInterceptor
    {
        public volatile bool Armed;

        public ManualResetEventSlim Committed { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            if (Armed && context.Property.Name == propertyName)
            {
                Committed.Set();
                if (!Release.Wait(WaitBudget))
                {
                    throw new TimeoutException("The test did not release the parked write.");
                }
            }
        }
    }
}

[InterceptorSubject]
public partial class TimestampedCounter
{
    public partial int Value { get; set; }
}

[InterceptorSubject]
public partial class Divider
{
    public partial int Numerator { get; set; }

    public partial int Denominator { get; set; }

    [Derived]
    public int Quotient => Numerator / Denominator;
}
