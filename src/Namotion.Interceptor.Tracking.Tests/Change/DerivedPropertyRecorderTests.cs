using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyRecorderTests
{
    private static DerivedPropertyRecorder CreateRecorder() => new();

    [Fact]
    public void StartRecording_SetsIsRecordingTrue()
    {
        // Arrange
        var recorder = CreateRecorder();

        // Act
        recorder.StartRecording();

        // Assert
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public void FinishRecording_SetsIsRecordingFalse()
    {
        // Arrange
        var recorder = CreateRecorder();
        recorder.StartRecording();

        // Act
        _ = recorder.FinishRecording();

        // Assert
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void TouchProperty_RecordsProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording();

        // Act
        recorder.TouchProperty(ref property);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Single(recorded.ToArray());
        Assert.Equal(property, recorded.ToArray()[0]);
    }

    [Fact]
    public void TouchProperty_DeduplicatesSameProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording();

        // Act - touch same property multiple times
        recorder.TouchProperty(ref property);
        recorder.TouchProperty(ref property);
        recorder.TouchProperty(ref property);
        var recorded = recorder.FinishRecording();

        // Assert - should only appear once
        Assert.Single(recorded.ToArray());
    }

    [Fact]
    public void TouchProperty_RecordsMultipleDistinctProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));

        recorder.StartRecording();

        // Act
        recorder.TouchProperty(ref firstNameProperty);
        recorder.TouchProperty(ref lastNameProperty);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, recorded.Length);
        Assert.Contains(firstNameProperty, recorded.ToArray());
        Assert.Contains(lastNameProperty, recorded.ToArray());
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
        recorder.StartRecording();
        recorder.TouchProperty(ref firstNameProperty);

        recorder.StartRecording(); // Nested
        recorder.TouchProperty(ref lastNameProperty);
        recorder.TouchProperty(ref fatherProperty);
        var innerRecorded = recorder.FinishRecording();

        var outerRecorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, innerRecorded.Length);
        Assert.Contains(lastNameProperty, innerRecorded.ToArray());
        Assert.Contains(fatherProperty, innerRecorded.ToArray());

        Assert.Single(outerRecorded.ToArray());
        Assert.Contains(firstNameProperty, outerRecorded.ToArray());
    }

    [Fact]
    public void Recording_GrowsBufferWhenNeeded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        recorder.StartRecording();

        // Act - add more properties than initial buffer size (8)
        var properties = new List<PropertyReference>();
        for (var i = 0; i < 20; i++)
        {
            var property = new PropertyReference(person, $"Prop{i}");
            properties.Add(property);
            recorder.TouchProperty(ref property);
        }

        var recorded = recorder.FinishRecording();

        // Assert - all properties recorded and match
        Assert.Equal(20, recorded.Length);
        for (var i = 0; i < properties.Count; i++)
        {
            Assert.Equal(properties[i], recorded[i]);
        }
    }

    [Fact]
    public void MultipleRecordingSessions_ReuseBuffers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        // Act - multiple sessions
        for (var session = 0; session < 10; session++)
        {
            recorder.StartRecording();
            for (var i = 0; i < 5; i++)
            {
                var property = new PropertyReference(person, $"Prop{i}");
                recorder.TouchProperty(ref property);
            }
            var recorded = recorder.FinishRecording();
            Assert.Equal(5, recorded.Length);
        }

        // Assert - should not throw, buffers reused
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void ClearLastRecording_SetsCountToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording();
        recorder.TouchProperty(ref property);
        _ = recorder.FinishRecording();

        // Act
        recorder.ClearLastRecording();

        // Assert - next recording session should start fresh
        recorder.StartRecording();
        var recorded = recorder.FinishRecording();
        Assert.Equal(0, recorded.Length);
    }
}
