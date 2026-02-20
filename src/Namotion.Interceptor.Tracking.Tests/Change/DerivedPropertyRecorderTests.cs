using System.Reflection;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyRecorderTests
{
    // Access internal recorder via reflection for direct testing
    private static DerivedPropertyRecorder CreateRecorder()
    {
        var type = typeof(DerivedPropertyRecorder);
        return (DerivedPropertyRecorder)Activator.CreateInstance(type, nonPublic: true)!;
    }

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
        var prop = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording();

        // Act
        recorder.TouchProperty(ref prop);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Single(recorded.ToArray());
        Assert.Equal(prop, recorded.ToArray()[0]);
    }

    [Fact]
    public void TouchProperty_DeduplicatesSameProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var prop = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording();

        // Act - touch same property multiple times
        recorder.TouchProperty(ref prop);
        recorder.TouchProperty(ref prop);
        recorder.TouchProperty(ref prop);
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
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        var prop2 = new PropertyReference(person, nameof(Person.LastName));

        recorder.StartRecording();

        // Act
        recorder.TouchProperty(ref prop1);
        recorder.TouchProperty(ref prop2);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, recorded.Length);
        Assert.Contains(prop1, recorded.ToArray());
        Assert.Contains(prop2, recorded.ToArray());
    }

    [Fact]
    public void NestedRecording_MaintainsSeparateFrames()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        var prop2 = new PropertyReference(person, nameof(Person.LastName));
        var prop3 = new PropertyReference(person, nameof(Person.Father));

        // Act - nested recording
        recorder.StartRecording();
        recorder.TouchProperty(ref prop1);

        recorder.StartRecording(); // Nested
        recorder.TouchProperty(ref prop2);
        recorder.TouchProperty(ref prop3);
        var innerRecorded = recorder.FinishRecording();

        var outerRecorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, innerRecorded.Length);
        Assert.Contains(prop2, innerRecorded.ToArray());
        Assert.Contains(prop3, innerRecorded.ToArray());

        Assert.Single(outerRecorded.ToArray());
        Assert.Contains(prop1, outerRecorded.ToArray());
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
        var props = new List<PropertyReference>();
        for (var i = 0; i < 20; i++)
        {
            var prop = new PropertyReference(person, $"Prop{i}");
            props.Add(prop);
            recorder.TouchProperty(ref prop);
        }

        var recorded = recorder.FinishRecording();

        // Assert - all properties recorded
        Assert.Equal(20, recorded.Length);
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
                var prop = new PropertyReference(person, $"Prop{i}");
                recorder.TouchProperty(ref prop);
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
        var prop = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording();
        recorder.TouchProperty(ref prop);
        _ = recorder.FinishRecording();

        // Act
        recorder.ClearLastRecording();

        // Assert - next recording session should start fresh
        recorder.StartRecording();
        var recorded = recorder.FinishRecording();
        Assert.Equal(0, recorded.Length);
    }
}
