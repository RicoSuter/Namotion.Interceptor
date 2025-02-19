﻿using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class ReadPropertyRecorderTests
{
    [Fact]
    public void WhenPropertyIsChanged_ThenItIsPartOfRecordedProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable()
            .WithReadPropertyRecorder();

        // Act
        var person = new Person(context);

        var recorder = context.GetService<ReadPropertyRecorder>().StartRecordingPropertyReadCalls();
        using (recorder)
        {
             var firstName = person.FirstName;
        }

        var lastName = person.LastName;

        // Assert
        Assert.Single(recorder.Properties);
        Assert.Contains(recorder.Properties, p => p.Name == "FirstName");
    }
}