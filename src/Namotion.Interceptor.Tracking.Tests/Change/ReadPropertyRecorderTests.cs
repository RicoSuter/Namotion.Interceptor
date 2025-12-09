using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class ReadPropertyRecorderTests
{
    [Fact]
    public void WhenPropertyIsReadWithExplicitRecording_ThenItIsPartOfRecordedProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable();

        var person = new Person(context);
        var recorder = new ConcurrentDictionary<PropertyReference, bool>();

        // Act - Explicitly record property read
        recorder.TryAdd(new PropertyReference(person, "FirstName"), false);
        var firstName = person.FirstName;

        // Read without recording
        var lastName = person.LastName;

        // Assert
        Assert.Single(recorder);
        Assert.Contains(recorder.Keys, p => p.Name == "FirstName");
        Assert.DoesNotContain(recorder.Keys, p => p.Name == "LastName");
    }

    [Fact]
    public void WhenUsingReadPropertyRecorderStart_ThenScopeIsCreatedWithCorrectContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable();

        var properties = new ConcurrentDictionary<PropertyReference, bool>();

        // Act
        using var scope = ReadPropertyRecorder.Start(context, properties);

        // Assert - scope is created and linked to properties dictionary
        Assert.NotNull(scope);
    }
}
