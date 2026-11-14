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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectIsRead_ThenReadIsObserved(bool hasContext)
    {
        // Arrange
        var subject = hasContext ? new ObservedSubject(InterceptorSubjectContext.Create()) : new ObservedSubject();
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

    [Fact]
    public void WhenWriteIsMadeInTheChainOfAnotherWrite_ThenInnerWriteIsObservedFirstAtTheNextDepth()
    {
        // Arrange
        var inner = new ObservedSubject();
        var depthsInChain = new List<int>();
        var context = InterceptorSubjectContext.Create().WithService(() => new CallbackWrites(() =>
        {
            depthsInChain.Add(PropertyAccessObservation.WriteDepth);
            inner.Value = 7;
        }));
        var outer = new ObservedSubject(context);

        // Act
        outer.Value = 5;

        // Assert
        Assert.Equal([1], depthsInChain);
        Assert.Equal(
            [(new PropertyReference(inner, nameof(ObservedSubject.Value)), 2), (new PropertyReference(outer, nameof(ObservedSubject.Value)), 1)],
            _observer.Writes.Select(write => (write.Property, write.Depth)));
        Assert.Equal(0, PropertyAccessObservation.WriteDepth);
    }

    [Fact]
    public void WhenWriteInTheChainOfAnotherWriteThrows_ThenItIsObservedAsFailedAndTheOuterWriteIsNot()
    {
        // Arrange
        var inner = new ObservedSubject(InterceptorSubjectContext.Create().WithService(() => new ThrowingWrites()));
        var context = InterceptorSubjectContext.Create().WithService(() => new CallbackWrites(() =>
        {
            Assert.Throws<InvalidOperationException>(() => inner.Value = 7);
        }));
        var outer = new ObservedSubject(context);

        // Act
        outer.Value = 5;

        // Assert
        Assert.Equal(
            [((IInterceptorSubject)inner, 2, true, false), ((IInterceptorSubject)outer, 1, false, true)],
            _observer.Writes.Select(write => (write.Property.Subject, write.Depth, write.HasFailed, write.IsWritten)));
        Assert.Equal(0, PropertyAccessObservation.WriteDepth);
    }

    [Fact]
    public void WhenWriteOfSubjectWithoutContextThrows_ThenWriteIsObservedAsFailedAndNotWritten()
    {
        // Arrange
        var subject = new ObservedSubject();

        // Act
        Assert.Throws<InvalidOperationException>(() => InterceptorExecutor.SetPropertyValueWithoutContext<int>(
            subject, nameof(ObservedSubject.Value), 5, (_, _) => throw new InvalidOperationException()));

        // Assert
        var write = Assert.Single(_observer.Writes);
        Assert.True(write.HasFailed);
        Assert.False(write.IsWritten);
        Assert.Equal(1, write.Depth);
        Assert.Equal(0, PropertyAccessObservation.WriteDepth);
    }

    [Fact]
    public void WhenReadObserverIsSetOnAnotherThread_ThenReadsOfThisThreadAreNotObserved()
    {
        // Arrange
        var subject = new ObservedSubject();
        var otherThread = new Thread(() =>
        {
            ObserveReads();
            _ = subject.Value;
        });

        // Act
        otherThread.Start();
        otherThread.Join();
        _ = subject.Value;

        // Assert
        Assert.Single(_observer.Reads);
        Assert.Null(PropertyAccessObservation.ReadObserver);
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

    private sealed class CallbackWrites(Action afterNext) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            afterNext();
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
