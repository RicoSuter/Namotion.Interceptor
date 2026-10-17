using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Recorder;

public class ReadPropertyRecorderTargetTests
{
    [Fact]
    public void WhenATargetIsSet_ThenItReceivesTheReadsAndTheScopesDoNot()
    {
        // Arrange
        var person = new Person(InterceptorSubjectContext.Create().WithReadPropertyRecorder());
        var target = new RecordingTarget();
        var scopeProperties = new ConcurrentDictionary<PropertyReference, bool>();

        // Act
        using (ReadPropertyRecorder.Start(scopeProperties))
        {
            using (ReadPropertyRecorder.RecordOnCurrentThread(target))
            {
                _ = person.FirstName;
            }

            _ = person.LastName;
        }

        // Assert
        Assert.Equal(["FirstName"], target.Reads.Select(property => property.Name));
        Assert.Equal(["LastName"], scopeProperties.Keys.Select(property => property.Name));
    }

    [Fact]
    public void WhenTargetsAreNested_ThenTheInnerOneReceivesItsReadsAndTheOuterOneIsRestored()
    {
        // Arrange
        var person = new Person(InterceptorSubjectContext.Create().WithReadPropertyRecorder());
        var outer = new RecordingTarget();
        var inner = new RecordingTarget();

        // Act
        using (ReadPropertyRecorder.RecordOnCurrentThread(outer))
        {
            using (ReadPropertyRecorder.RecordOnCurrentThread(inner))
            {
                _ = person.FirstName;
            }

            _ = person.LastName;
        }

        _ = person.FirstName;

        // Assert
        Assert.Equal(["FirstName"], inner.Reads.Select(property => property.Name));
        Assert.Equal(["LastName"], outer.Reads.Select(property => property.Name));
    }

    [Fact]
    public void WhenTargetScopesAreDisposed_ThenNoTargetIsCounted()
    {
        // Arrange
        var countBefore = ReadPropertyRecorder.ThreadTargetCount;

        // Act
        using (ReadPropertyRecorder.RecordOnCurrentThread(new RecordingTarget()))
        {
            using (ReadPropertyRecorder.RecordOnCurrentThread(new RecordingTarget()))
            {
                Assert.Equal(countBefore + 2, ReadPropertyRecorder.ThreadTargetCount);
            }
        }

        default(ReadPropertyRecorderTargetScope).Dispose();

        // Assert
        Assert.Equal(countBefore, ReadPropertyRecorder.ThreadTargetCount);
    }

    [Fact]
    public void WhenATargetIsSetOnOneThread_ThenReadsOnAnotherThreadDoNotReachIt()
    {
        // Arrange
        var person = new Person(InterceptorSubjectContext.Create().WithReadPropertyRecorder());
        var target = new RecordingTarget();

        // Act
        using (ReadPropertyRecorder.RecordOnCurrentThread(target))
        {
            var thread = new Thread(() => _ = person.FirstName);
            thread.Start();
            thread.Join();
        }

        // Assert
        Assert.Empty(target.Reads);
    }

    private sealed class RecordingTarget : IReadPropertyRecorderTarget
    {
        public List<PropertyReference> Reads { get; } = [];

        public void OnRead(PropertyReference property) => Reads.Add(property);
    }
}
