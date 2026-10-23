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

    [Fact]
    public void WhenWriteCommitsBeforeTheValueRead_ThenThatWritesPairIsReturned()
    {
        // Arrange
        var subject = CreateCounter(out var interceptor);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));
        Write(subject, 1, FirstTimestamp);
        interceptor.BeforeValueRead = () => Write(subject, 2, SecondTimestamp);
        interceptor.Armed = true;

        // Act
        var value = property.GetValue(out var metadata);

        // Assert
        Assert.Equal(2, value);
        Assert.Equal(SecondTimestamp, metadata.WriteTimestamp);
    }

    /// <summary>
    /// An int is a type a read could take without the subject's lock, and a value read that skips the lock
    /// fails this test unless the metadata read takes it, so the pairing must not rely on the read terminal.
    /// </summary>
    [Fact]
    public async Task WhenWritesRunConcurrently_ThenMetadataNeverDescribesAnEarlierWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new TimestampedCounter(context);
        var property = subject.GetPropertyReference(nameof(TimestampedCounter.Value));
        const int writes = 300_000;
        Write(subject, -1, FirstTimestamp.AddTicks(-1));

        var reads = 0;
        using var readerStarted = new ManualResetEventSlim(false);
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            if (!readerStarted.Wait(WaitBudget))
            {
                throw new TimeoutException("The reader did not start.");
            }

            // Keeps writing until the reader has read at least once, within a bound, so a descheduled reader
            // cannot leave the test with nothing checked.
            var index = 0;
            while (index < writes || (Volatile.Read(ref reads) == 0 && index < writes * 10))
            {
                index++;
                Write(subject, index, FirstTimestamp.AddTicks(index));
            }
        }, "writer");

        // Act
        var violations = 0;
        readerStarted.Set();
        while (!writer.IsCompleted)
        {
            var value = (int)property.GetValue(out var metadata)!;
            var writeIndex = (metadata.WriteTimestamp!.Value - FirstTimestamp).Ticks;
            if (writeIndex < value)
            {
                violations++;
            }

            Volatile.Write(ref reads, reads + 1);
        }

        await writer;

        // Assert
        Assert.True(reads > 0, "The reader did not read while the writer ran.");
        Assert.Equal(0, violations);
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
        _ = property.GetValue(out _);

        // Assert
        Assert.False(observer.SyncRootHeld);
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

    [Fact]
    public void WhenDependencyIsWrittenWithoutTimestamp_ThenDerivedValueComesWithoutTimestamp()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var fullName = person.GetPropertyReference(nameof(Person.FullName));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            person.LastName = "Doe";
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            person.FirstName = "Jane";
        }

        // Act
        var value = fullName.GetValue(out var metadata);

        // Assert
        Assert.Equal("Jane Doe", value);
        Assert.Null(fullName.TryGetWriteTimestamp());
        Assert.Null(metadata.WriteTimestamp);
    }

    [Fact]
    public async Task WhenDerivedPropertyIsReadWhileARecalculationAfterAWriteWithoutTimestampIsPending_ThenGetterValueComesWithoutTimestamp()
    {
        // Arrange
        var parking = new ParkingWriteInterceptor(nameof(Person.FirstName));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(parking);

        var person = new Person(context);
        var fullName = person.GetPropertyReference(nameof(Person.FullName));

        using (SubjectChangeContext.WithChangedTimestamp(FirstTimestamp))
        {
            person.LastName = "Doe";
            person.FirstName = "John";
        }

        parking.Armed = true;
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(null))
            {
                person.FirstName = "Jane";
            }
        }, "writer");

        Assert.True(parking.Committed.Wait(WaitBudget), "The writer did not reach the parked commit.");

        // Act
        var separateTimestamp = fullName.TryGetWriteTimestamp();
        var pairedValue = fullName.GetValue(out var pairedMetadata);

        parking.Release.Set();
        await writer.WaitAsync(WaitBudget);

        // Assert
        Assert.Equal(FirstTimestamp, separateTimestamp);
        Assert.Equal("Jane Doe", pairedValue);
        Assert.Null(pairedMetadata.WriteTimestamp);
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

    private static TimestampedCounter CreateCounter(out WritingReadInterceptor interceptor)
    {
        var writing = new WritingReadInterceptor(nameof(TimestampedCounter.Value));
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

    // Once armed, runs the configured write inside the next value read of the named property, before the value is read.
    private sealed class WritingReadInterceptor(string propertyName) : IReadInterceptor
    {
        public volatile bool Armed;

        public Action? BeforeValueRead { get; set; }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            if (!Armed || context.Property.Name != propertyName)
            {
                return next(ref context);
            }

            Armed = false;
            BeforeValueRead?.Invoke();
            return next(ref context);
        }
    }

    // Once armed, parks the next writer after the terminal has committed and before the derived handler recalculates.
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
                Armed = false;
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
public partial class PlainFieldDevice
{
    private string? _version;

    [Derived]
    public string? SoftwareVersion => _version;

    public void SetVersion(string version) => _version = version;
}
