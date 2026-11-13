using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

[InterceptorSubject]
public partial class ObservedSubject
{
    public partial int Value { get; set; }
}

[Collection(PropertyAccessObservationCollection.Name)]
public sealed class PropertyAccessObservationTests : IDisposable
{
    private readonly RecordingObserver _observer = new();

    public PropertyAccessObservationTests()
    {
        PropertyAccessObservation.ReplaceWriteObserverForTests(_observer);
    }

    public void Dispose()
    {
        PropertyAccessObservation.ReadObserver = null;
        PropertyAccessObservation.ReplaceWriteObserverForTests(null);
    }

    // The read observer is thread-local, so it is installed on the thread that runs the test body.
    private void ObserveReads() => PropertyAccessObservation.ReadObserver = _observer;

    [Fact]
    public void WhenSubjectWithoutContextIsRead_ThenReadIsObserved()
    {
        // Arrange
        var subject = new ObservedSubject();
        ObserveReads();

        // Act
        _ = subject.Value;

        // Assert
        Assert.Equal([new PropertyReference(subject, nameof(ObservedSubject.Value))], _observer.Reads);
    }

    [Fact]
    public void WhenSubjectWithContextIsRead_ThenReadIsObserved()
    {
        // Arrange
        var subject = new ObservedSubject(InterceptorSubjectContext.Create());
        ObserveReads();

        // Act
        _ = subject.Value;

        // Assert
        Assert.Equal([new PropertyReference(subject, nameof(ObservedSubject.Value))], _observer.Reads);
    }

    [Fact]
    public void WhenSubjectWithoutContextIsWritten_ThenWriteIsObservedOnceAtDepthOne()
    {
        // Arrange
        var subject = new ObservedSubject();

        // Act
        subject.Value = 5;

        // Assert
        var write = Assert.Single(_observer.Writes);
        Assert.Equal(new PropertyReference(subject, nameof(ObservedSubject.Value)), write.Property);
        Assert.True(write.IsWritten);
        Assert.Equal(1, write.Depth);
        Assert.False(write.HasFailed);
        Assert.Equal(0, PropertyAccessObservation.WriteDepth);
    }

    [Fact]
    public void WhenObserverIsInstalledDuringWriteWithoutContext_ThenWriteIsObserved()
    {
        // Arrange
        PropertyAccessObservation.ReplaceWriteObserverForTests(null);
        var subject = new ObservedSubject();

        // Act
        InterceptorExecutor.SetPropertyValueWithoutContext(
            subject, nameof(ObservedSubject.Value), 5, (_, _) => PropertyAccessObservation.ReplaceWriteObserverForTests(_observer));

        // Assert
        var write = Assert.Single(_observer.Writes);
        Assert.Equal(new PropertyReference(subject, nameof(ObservedSubject.Value)), write.Property);
        Assert.True(write.IsWritten);
        Assert.Equal(1, write.Depth);
        Assert.False(write.HasFailed);
    }

    [Fact]
    public void WhenSubjectWithContextIsWritten_ThenWriteIsObservedAfterTheChainWithItsTimestamp()
    {
        // Arrange
        var subject = new ObservedSubject(InterceptorSubjectContext.Create());

        // Act
        subject.Value = 5;

        // Assert
        var write = Assert.Single(_observer.Writes);
        Assert.True(write.IsWritten);
        Assert.NotEqual(0, write.RawTimestamp);
        Assert.Equal(1, write.Depth);
    }

    [Fact]
    public void WhenWriteIsVetoed_ThenWriteIsObservedAsNotWritten()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new VetoAllWrites());
        var subject = new ObservedSubject(context);

        // Act
        subject.Value = 5;

        // Assert
        Assert.False(Assert.Single(_observer.Writes).IsWritten);
    }

    [Fact]
    public void WhenChainThrows_ThenWriteIsObservedAsFailedAndDepthIsRestored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new ThrowingWrites());
        var subject = new ObservedSubject(context);

        // Act
        Assert.Throws<InvalidOperationException>(() => subject.Value = 5);

        // Assert
        Assert.True(Assert.Single(_observer.Writes).HasFailed);
        Assert.Equal(0, PropertyAccessObservation.WriteDepth);
    }

    private sealed class RecordingObserver : IPropertyReadObserver, IPropertyWriteObserver
    {
        public List<PropertyReference> Reads { get; } = [];
        public List<PropertyWriteObservation> Writes { get; } = [];
        public void OnPropertyRead(in PropertyReference property) => Reads.Add(property);
        public void OnPropertyWritten(in PropertyWriteObservation write) => Writes.Add(write);
    }

    private sealed class VetoAllWrites : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            // Vetoes: next is not called, so nothing lands.
        }
    }

    private sealed class ThrowingWrites : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
            => throw new InvalidOperationException();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PropertyAccessObservationCollection
{
    public const string Name = "PropertyAccessObservation";

    private PropertyAccessObservationCollection()
    {
    }
}
