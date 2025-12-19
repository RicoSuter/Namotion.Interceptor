using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class ReadPropertyRecorderTests
{
    [Fact]
    public void WhenPropertyIsRead_ThenItIsAutomaticallyRecorded()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder();

        var person = new Person(context);
        var properties = new ConcurrentDictionary<PropertyReference, bool>();

        // Act
        using (ReadPropertyRecorder.Start(properties))
        {
            _ = person.FirstName;
            _ = person.LastName;
        }

        // Assert
        Assert.Equal(2, properties.Count);
        Assert.Contains(properties.Keys, p => p.Name == "FirstName");
        Assert.Contains(properties.Keys, p => p.Name == "LastName");
    }

    [Fact]
    public void WhenScopeIsDisposed_ThenRecordingStops()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder();

        var person = new Person(context);
        var properties = new ConcurrentDictionary<PropertyReference, bool>();

        // Act
        using (ReadPropertyRecorder.Start(properties))
        {
            _ = person.FirstName;
        }
        // After dispose
        _ = person.LastName;

        // Assert
        Assert.Single(properties);
        Assert.Contains(properties.Keys, p => p.Name == "FirstName");
    }

    [Fact]
    public void WhenMultipleScopesAreActive_ThenAllReceivePropertyReads()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder();

        var person = new Person(context);
        var properties1 = new ConcurrentDictionary<PropertyReference, bool>();
        var properties2 = new ConcurrentDictionary<PropertyReference, bool>();

        // Act
        using (ReadPropertyRecorder.Start(properties1))
        using (ReadPropertyRecorder.Start(properties2))
        {
            _ = person.FirstName;
        }

        // Assert
        Assert.Single(properties1);
        Assert.Single(properties2);
    }

    [Fact]
    public async Task WhenDifferentAsyncContexts_ThenScopesAreIsolated()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder();

        var person = new Person(context);
        var properties1 = new ConcurrentDictionary<PropertyReference, bool>();
        var properties2 = new ConcurrentDictionary<PropertyReference, bool>();

        // Act - Start scopes in separate async contexts
        var task1 = Task.Run(() =>
        {
            using (ReadPropertyRecorder.Start(properties1))
            {
                _ = person.FirstName;
            }
        });

        var task2 = Task.Run(() =>
        {
            using (ReadPropertyRecorder.Start(properties2))
            {
                _ = person.LastName;
            }
        });

        await Task.WhenAll(task1, task2);

        // Assert - Each scope only recorded its own property
        Assert.Single(properties1);
        Assert.Single(properties2);
        Assert.Contains(properties1.Keys, p => p.Name == "FirstName");
        Assert.Contains(properties2.Keys, p => p.Name == "LastName");
    }

    [Fact]
    public async Task WhenAwaitIsUsed_ThenScopeFlowsCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder();

        var person = new Person(context);
        var properties = new ConcurrentDictionary<PropertyReference, bool>();

        // Act - Read properties before and after await
        using (ReadPropertyRecorder.Start(properties))
        {
            _ = person.FirstName;
            await Task.Delay(1);
            _ = person.LastName;
        }

        // Assert - Both reads recorded (AsyncLocal flowed across await)
        Assert.Equal(2, properties.Count);
        Assert.Contains(properties.Keys, p => p.Name == "FirstName");
        Assert.Contains(properties.Keys, p => p.Name == "LastName");
    }
}
