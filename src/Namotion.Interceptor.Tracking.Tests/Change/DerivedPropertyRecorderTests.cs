using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public sealed class DerivedPropertyRecorderTests : IDisposable
{
    public void Dispose()
    {
        // A recording that a test leaves open keeps the recorder installed as the thread's read observer.
        PropertyAccessObservation.ReadObserver = null;
    }

    private static DerivedPropertyRecorder CreateRecorder() => new();

    private static PropertyReference[] GetProperties(ReadOnlySpan<RecordedDependency> recorded)
    {
        var properties = new PropertyReference[recorded.Length];
        for (var i = 0; i < recorded.Length; i++)
        {
            properties[i] = recorded[i].Property;
        }

        return properties;
    }

    private static PropertyReference CreateSelf(IInterceptorSubject subject) =>
        new(subject, "__SelfUnderTest");

    [Fact]
    public void StartRecording_SetsIsRecordingTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        // Act
        recorder.StartRecording(CreateSelf(person));

        // Assert
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public void FinishRecording_SetsIsRecordingFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        recorder.StartRecording(CreateSelf(person));

        // Act
        _ = recorder.FinishRecording();

        // Assert
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void OnPropertyRead_RecordsProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(CreateSelf(person));

        // Act
        recorder.OnPropertyRead(in property);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Single(GetProperties(recorded));
        Assert.Equal(property, recorded[0].Property);
    }

    [Fact]
    public void OnPropertyRead_DeduplicatesSamePropertyAndKeepsFirstVersion()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var data = property.GetDerivedPropertyData();

        recorder.StartRecording(CreateSelf(person));

        // Act - read the same property multiple times, with a write between the reads
        recorder.OnPropertyRead(in property);
        Interlocked.Increment(ref data.Version);
        recorder.OnPropertyRead(in property);
        recorder.OnPropertyRead(in property);
        var recorded = recorder.FinishRecording();

        // Assert - should only appear once, with the version of the first read
        var dependency = Assert.Single(recorded.ToArray());
        Assert.Equal(property, dependency.Property);
        Assert.Same(data, dependency.Data);
        Assert.Equal(0, dependency.Version);
    }

    [Fact]
    public void OnPropertyRead_RecordsMultipleDistinctProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));

        recorder.StartRecording(CreateSelf(person));

        // Act
        recorder.OnPropertyRead(in firstNameProperty);
        recorder.OnPropertyRead(in lastNameProperty);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, recorded.Length);
        Assert.Contains(firstNameProperty, GetProperties(recorded));
        Assert.Contains(lastNameProperty, GetProperties(recorded));
    }

    [Fact]
    public void OnPropertyRead_SkipsSelfReference()
    {
        // The derived property's outer interceptor-chain read fires ReadProperty on itself,
        // which must not be recorded as a dependency.

        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var selfProperty = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(selfProperty);

        // Act - touch both the self-ref and a real dependency
        recorder.OnPropertyRead(in selfProperty);
        recorder.OnPropertyRead(in firstNameProperty);
        var recorded = recorder.FinishRecording();

        // Assert - only the real dependency is recorded
        Assert.Single(GetProperties(recorded));
        Assert.Equal(firstNameProperty, recorded[0].Property);
    }

    [Fact]
    public void NestedRecording_EachFrameSkipsOnlyItsOwnSelf()
    {
        // Nested recording: outer frame records inner's property; inner frame records its own deps.
        // Each frame must exclude only its own self-ref, not the other's.

        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var outerSelf = new PropertyReference(person, "OuterDerived");
        var innerSelf = new PropertyReference(person, "InnerDerived");
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));

        // Act: the outer frame touches the inner derived property, while the inner frame touches itself.
        recorder.StartRecording(outerSelf);
        recorder.OnPropertyRead(in innerSelf);

        recorder.StartRecording(innerSelf);
        recorder.OnPropertyRead(in innerSelf);       // filtered (inner's self)
        recorder.OnPropertyRead(in firstNameProperty);
        var innerRecorded = recorder.FinishRecording();

        var outerRecorded = recorder.FinishRecording();

        // Assert
        Assert.Single(GetProperties(innerRecorded));
        Assert.Equal(firstNameProperty, innerRecorded[0].Property);

        Assert.Single(GetProperties(outerRecorded));
        Assert.Equal(innerSelf, outerRecorded[0].Property);
    }

    [Fact]
    public void NestedRecording_MaintainsSeparateFrames()
    {
        // When a derived property's getter triggers recalculation of another derived property,
        // StartRecording() nests. Each frame must record only its own dependencies so that
        // StoreRecordedTouchedProperties builds the correct RequiredProperties per property.
        // Without frame isolation, outer properties would incorrectly include inner dependencies.

        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));

        // Act - nested recording
        recorder.StartRecording(CreateSelf(person));
        recorder.OnPropertyRead(in firstNameProperty);

        recorder.StartRecording(CreateSelf(person)); // Nested
        recorder.OnPropertyRead(in lastNameProperty);
        recorder.OnPropertyRead(in fatherProperty);
        var innerRecorded = recorder.FinishRecording();

        var outerRecorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, innerRecorded.Length);
        Assert.Contains(lastNameProperty, GetProperties(innerRecorded));
        Assert.Contains(fatherProperty, GetProperties(innerRecorded));

        Assert.Single(GetProperties(outerRecorded));
        Assert.Contains(firstNameProperty, GetProperties(outerRecorded));
    }

    [Fact]
    public void Recording_GrowsBufferWhenNeeded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        recorder.StartRecording(CreateSelf(person));

        // Act - add more properties than initial buffer size (8)
        var properties = new List<PropertyReference>();
        for (var i = 0; i < 20; i++)
        {
            var property = new PropertyReference(person, $"Prop{i}");
            properties.Add(property);
            recorder.OnPropertyRead(in property);
        }

        var recorded = recorder.FinishRecording();

        // Assert - all properties recorded and match
        Assert.Equal(20, recorded.Length);
        for (var i = 0; i < properties.Count; i++)
        {
            Assert.Equal(properties[i], recorded[i].Property);
        }
    }

    [Fact]
    public void MultipleRecordingSessions_ReuseBuffers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        // Act - the first session is the one that rents the pooled buffer
        recorder.StartRecording(CreateSelf(person));
        TouchProperties(recorder, person, count: 5);
        var firstRecording = recorder.FinishRecording();

        // Identity of the backing storage is what proves reuse. Asserting on the recorded
        // values instead would pass just as happily if every session rented a fresh buffer,
        // which is the regression this test exists to catch.
        ref var firstSlot = ref MemoryMarshal.GetReference(firstRecording);

        Assert.Equal(5, firstRecording.Length);

        // Act - every later session stays within the rented capacity, so none may rent again
        for (var session = 1; session < 10; session++)
        {
            recorder.StartRecording(CreateSelf(person));
            TouchProperties(recorder, person, count: 5);
            var recording = recorder.FinishRecording();

            // Assert
            Assert.Equal(5, recording.Length);
            Assert.True(
                Unsafe.AreSame(ref firstSlot, ref MemoryMarshal.GetReference(recording)),
                $"Session {session} rented a new buffer instead of reusing the first session's.");
        }

        // Assert
        Assert.False(recorder.IsRecording);
    }

    private static void TouchProperties(DerivedPropertyRecorder recorder, IInterceptorSubject subject, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var property = new PropertyReference(subject, $"Prop{i}");
            recorder.OnPropertyRead(in property);
        }
    }

    [Fact]
    public void ClearLastRecording_SetsCountToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(CreateSelf(person));
        recorder.OnPropertyRead(in property);
        _ = recorder.FinishRecording();

        // Act
        recorder.ClearLastRecording();

        // Assert - next recording session should start fresh
        recorder.StartRecording(CreateSelf(person));
        var recorded = recorder.FinishRecording();
        Assert.Equal(0, recorded.Length);
    }
}
