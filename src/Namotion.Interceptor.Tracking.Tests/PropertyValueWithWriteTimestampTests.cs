using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests;

public class PropertyValueWithWriteTimestampTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondTimestamp = FirstTimestamp.AddMinutes(1);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Read interceptors run outside the subject's lock, so a write can commit between the state snapshot
    /// taken before the value read and the one taken after it, on either side of the read terminal. The
    /// snapshots then differ and the read runs again, returning the pair that commit produced.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenWriteCommitsWhileReadInterceptorsRun_ThenReadRetriesAndReturnsTheCommittedPair(bool beforeValueRead)
    {
        // Arrange
        var subject = CreateCounter(out var interceptor);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));
        Write(subject, 1, FirstTimestamp);

        Action<int> write = _ => Write(subject, 2, SecondTimestamp);
        if (beforeValueRead)
        {
            interceptor.BeforeValueRead = write;
        }
        else
        {
            interceptor.AfterValueRead = write;
        }

        interceptor.Armed = true;

        // Act
        var value = property.GetValue(out var metadata);

        // Assert
        Assert.Equal(2, value);
        Assert.Equal(SecondTimestamp, metadata.WriteTimestamp);
        Assert.Equal(2, interceptor.Passes);
    }

    /// <summary>
    /// A write with the timestamp the state had before the read commits after the value read, so both
    /// snapshots carry that timestamp although the value came from a write with another one. Only the
    /// commit revision tells the snapshots apart.
    /// </summary>
    [Fact]
    public void WhenWritesAroundTheValueReadShareTheTimestampBefore_ThenRevisionForcesTheRetry()
    {
        // Arrange
        var subject = CreateCounter(out var interceptor);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));
        Write(subject, 1, FirstTimestamp);

        interceptor.BeforeValueRead = _ => Write(subject, 2, SecondTimestamp);
        interceptor.AfterValueRead = _ => Write(subject, 3, FirstTimestamp);
        interceptor.Armed = true;

        // Act
        var value = property.GetValue(out var metadata);

        // Assert
        Assert.Equal(3, value);
        Assert.Equal(FirstTimestamp, metadata.WriteTimestamp);
        Assert.Equal(2, interceptor.Passes);
    }

    /// <summary>
    /// When a write commits after every value read, the retries run out and the read returns its last
    /// value with the state taken after it, which then describes the write that followed that value.
    /// </summary>
    [Fact]
    public void WhenWriteCommitsAfterEveryValueRead_ThenLastValueIsReturnedWithTheNewerTimestamp()
    {
        // Arrange
        var subject = CreateCounter(out var interceptor);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));
        Write(subject, 0, FirstTimestamp);

        interceptor.AfterValueRead = pass => Write(subject, pass, FirstTimestamp.AddMinutes(pass));
        interceptor.RepeatsOnEveryPass = true;
        interceptor.Armed = true;

        // Act
        var value = property.GetValue(out var metadata);

        // Assert
        var passes = PropertyReference.MaxReadRetries + 1;
        Assert.Equal(passes, interceptor.Passes);
        Assert.Equal(passes - 1, value);
        Assert.Equal(FirstTimestamp.AddMinutes(passes), metadata.WriteTimestamp);
    }

    [Fact]
    public void WhenStoredPropertyIsReadWithMetadata_ThenNoReadInterceptorRunsUnderSyncRoot()
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
        Assert.False(syncRootHeldForPairedRead);
    }

    [Fact]
    public async Task WhenStoredPropertyIsReadInsideTransaction_ThenPendingValueComesWithTheCommittedTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var subject = new TimestampedCounter(context);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));
        Write(subject, 1, FirstTimestamp);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        Write(subject, 2, SecondTimestamp);
        var value = property.GetValue(out var metadata);

        // Assert
        Assert.Equal(2, value);
        Assert.Equal(FirstTimestamp, metadata.WriteTimestamp);
    }

    /// <summary>
    /// A derived getter recomputes from its dependencies as soon as they are stored, while the derived
    /// property's own timestamp is stamped by the recalculation that follows. Parking the trigger write
    /// between the two leaves the getter ahead of that timestamp; the paired read returns the getter's
    /// value with the dependency's timestamp, which the property's own timestamp read separately lacks.
    /// A derived property over another derived property records the stored properties that getter reads
    /// as its own dependencies, so it needs no deeper lookup.
    /// </summary>
    [Theory]
    [InlineData(nameof(Person.FullName), "Jane")]
    [InlineData(nameof(Person.FullNameWithPrefix), "Mr. Jane")]
    public async Task WhenDerivedPropertyIsReadWhileItsRecalculationIsPending_ThenGetterValueComesWithTheDependencyWriteTimestamp(
        string propertyName, string expectedValue)
    {
        // Arrange
        var parking = new ParkingWriteInterceptor(nameof(Person.FirstName));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(parking);

        var person = new Person(context);
        var property = person.GetPropertyReference(propertyName);

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
        var separateTimestamp = property.TryGetWriteTimestamp();
        var pairedValue = property.GetValue(out var pairedMetadata);

        parking.Release.Set();
        await writer.WaitAsync(WaitBudget);

        var settledValue = property.GetValue(out var settledMetadata);

        // Assert
        Assert.Equal(FirstTimestamp, separateTimestamp);
        Assert.Equal(expectedValue, pairedValue);
        Assert.Equal(SecondTimestamp, pairedMetadata.WriteTimestamp);
        Assert.Equal(expectedValue, settledValue);
        Assert.Equal(SecondTimestamp, settledMetadata.WriteTimestamp);
    }

    /// <summary>
    /// A dependency on another subject is recorded like any other, so the paired read reaches that
    /// subject's write state for the timestamp while the recalculation is still pending.
    /// </summary>
    [Fact]
    public async Task WhenDerivedPropertyDependsOnAnotherSubject_ThenPairedReadCarriesThatSubjectsWriteTimestamp()
    {
        // Arrange
        var parking = new ParkingWriteInterceptor(nameof(Tire.Pressure));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(parking);

        var car = new Car(context);
        var averagePressure = car.GetPropertyReference(nameof(Car.AveragePressure));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            foreach (var tire in car.Tires)
            {
                tire.Pressure = 2m;
            }
        }

        parking.Armed = true;
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(SecondTimestamp))
            {
                car.Tires[0].Pressure = 6m;
            }
        }, "writer");

        Assert.True(parking.Committed.Wait(WaitBudget), "The writer did not reach the parked commit.");

        // Act
        var separateTimestamp = averagePressure.TryGetWriteTimestamp();
        var pairedValue = averagePressure.GetValue(out var pairedMetadata);

        parking.Release.Set();
        await writer.WaitAsync(WaitBudget);

        // Assert
        Assert.Equal(FirstTimestamp, separateTimestamp);
        Assert.Equal(3m, pairedValue);
        Assert.Equal(SecondTimestamp, pairedMetadata.WriteTimestamp);
    }

    /// <summary>
    /// A recalculation requested after the dependency writes stamps the derived property later than any
    /// of them, and that own timestamp is the one the paired read carries.
    /// </summary>
    [Fact]
    public void WhenDerivedPropertyIsRecalculatedAfterItsDependencyWrites_ThenPairedReadCarriesItsOwnNewerTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var fullName = person.GetPropertyReference(nameof(Person.FullName));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(SecondTimestamp))
        {
            fullName.RecalculateDerivedProperty();
        }

        // Act
        var value = fullName.GetValue(out var metadata);

        // Assert
        Assert.Equal("John", value);
        Assert.Equal(SecondTimestamp, fullName.TryGetWriteTimestamp());
        Assert.Equal(SecondTimestamp, metadata.WriteTimestamp);
    }

    /// <summary>
    /// The terminal of a derived-with-setter write stamps the property's write state before the recalculation
    /// that follows commits the value that write produces. While that recalculation is pending the getter
    /// already returns the new value, and the write timestamp paired with it is the one that write stamped.
    /// </summary>
    [Fact]
    public async Task WhenDerivedPropertyWithSetterIsReadWhileItsRecalculationIsPending_ThenGetterValueComesWithTheWriteTimestamp()
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
        await writer.WaitAsync(WaitBudget);

        var settledValue = nickname.GetValue(out var settledMetadata);

        // Assert
        Assert.Equal(SecondTimestamp, separateTimestamp);
        Assert.Equal("Jane", pairedValue);
        Assert.Equal(SecondTimestamp, pairedMetadata.WriteTimestamp);
        Assert.Equal("Jane", settledValue);
        Assert.Equal(SecondTimestamp, settledMetadata.WriteTimestamp);
    }

    /// <summary>
    /// Without an equality check, rewriting a derived-with-setter property's value stamps its write state
    /// before the recalculation commits the equal value that write produces. The paired read returns the
    /// timestamp of that rewrite, not the older one the previous recalculation committed with an equal value.
    /// </summary>
    [Fact]
    public async Task WhenDerivedPropertyWithSetterIsRewrittenWithAnEqualValue_ThenValueComesWithTheRewriteTimestamp()
    {
        // Arrange
        var parking = new ParkingWriteInterceptor(nameof(DerivedSetterPerson.Nickname));
        var context = InterceptorSubjectContext.Create().WithDerivedPropertyChangeDetection();
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
                person.Nickname = "John";
            }
        }, "writer");

        Assert.True(parking.Committed.Wait(WaitBudget), "The writer did not reach the parked commit.");

        // Act
        var separateTimestamp = nickname.TryGetWriteTimestamp();
        var pairedValue = nickname.GetValue(out var pairedMetadata);

        parking.Release.Set();
        await writer.WaitAsync(WaitBudget);

        var settledValue = nickname.GetValue(out var settledMetadata);

        // Assert
        Assert.Equal(SecondTimestamp, separateTimestamp);
        Assert.Equal("John", pairedValue);
        Assert.Equal(SecondTimestamp, pairedMetadata.WriteTimestamp);
        Assert.Equal("John", settledValue);
        Assert.Equal(SecondTimestamp, settledMetadata.WriteTimestamp);
    }

    /// <summary>
    /// A derived property over a field the interceptor cannot see is never recalculated, so its timestamp
    /// is the one from attach. The paired read still returns what the getter computes now.
    /// </summary>
    [Fact]
    public void WhenDerivedPropertyReadsAPlainFieldSetAfterAttach_ThenCurrentGetterValueIsReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        PlainFieldDevice device;
        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            device = new PlainFieldDevice(context);
        }

        var softwareVersion = device.GetPropertyReference(nameof(PlainFieldDevice.SoftwareVersion));
        device.SetVersion("1.2.3");

        // Act
        var value = softwareVersion.GetValue(out var metadata);

        // Assert
        Assert.Equal("1.2.3", value);
        Assert.Equal(softwareVersion.TryGetWriteTimestamp(), metadata.WriteTimestamp);
        Assert.Equal(FirstTimestamp, metadata.WriteTimestamp);
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
    public void WhenDerivedPropertyIsDetached_ThenGetterIsInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;
        child.FirstName = "John";
        parent.Father = null;
        var fullName = child.GetPropertyReference(nameof(Person.FullName));

        // Act
        var value = fullName.GetValue(out _);

        // Assert
        Assert.Equal("John", value);
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

    private static TimestampedCounter CreateCounter(out ConcurrentlyWritingReadInterceptor interceptor)
    {
        var writing = new ConcurrentlyWritingReadInterceptor(nameof(TimestampedCounter.Value));
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writing, _ => false);

        interceptor = writing;
        return new TimestampedCounter(context);
    }

    private static void Write(TimestampedCounter subject, int value, DateTimeOffset timestamp)
    {
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            subject.Value = value;
        }
    }

    // Once armed, commits the configured writes on another thread around the value read of the named
    // property, on the first pass only unless told to repeat, so a retried read can settle. The writes
    // are joined with a bound: a read that held the subject's lock across this interceptor would block
    // them forever.
    private sealed class ConcurrentlyWritingReadInterceptor(string propertyName) : IReadInterceptor
    {
        private int _passes;

        public volatile bool Armed;

        public bool RepeatsOnEveryPass { get; set; }

        public Action<int>? BeforeValueRead { get; set; }

        public Action<int>? AfterValueRead { get; set; }

        public int Passes => Volatile.Read(ref _passes);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            if (!Armed || context.Property.Name != propertyName)
            {
                return next(ref context);
            }

            var pass = Interlocked.Increment(ref _passes);
            var writes = RepeatsOnEveryPass || pass == 1;
            if (writes)
            {
                WriteOnOtherThread(BeforeValueRead, pass);
            }

            var value = next(ref context);

            if (writes)
            {
                WriteOnOtherThread(AfterValueRead, pass);
            }

            return value;
        }

        private static void WriteOnOtherThread(Action<int>? write, int pass)
        {
            if (write is null)
            {
                return;
            }

            var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => write(pass), "writer");
            if (!writer.Wait(WaitBudget))
            {
                throw new TimeoutException("The concurrent write did not commit while the read interceptor ran.");
            }
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

[InterceptorSubject]
public partial class PlainFieldDevice
{
    private string? _version;

    [Derived]
    public string? SoftwareVersion => _version;

    public void SetVersion(string version) => _version = version;
}
