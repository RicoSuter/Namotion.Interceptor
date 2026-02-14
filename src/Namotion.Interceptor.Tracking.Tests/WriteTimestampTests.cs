using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class WriteTimestampTests
{
    [Fact]
    public void WriteTimestamp_BeforeAnyWrite_ShouldBeNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Act
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();

        // Assert
        Assert.Null(timestamp);
    }

    [Fact]
    public void WriteTimestamp_AfterWrite_ShouldBeSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var before = DateTimeOffset.UtcNow;

        // Act
        person.FirstName = "John";

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
    }

    [Fact]
    public void WriteTimestamp_WithExplicitTimestamp_ShouldMatchExplicitValue()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-100);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            person.FirstName = "John";
        }

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.Equal(explicitTimestamp, timestamp);
    }

    [Fact]
    public void WriteTimestamp_SecondWriteOverwritesFirst()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstTimestamp = DateTimeOffset.UtcNow.AddDays(-100);
        var secondTimestamp = DateTimeOffset.UtcNow.AddDays(-50);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            person.FirstName = "Jane";
        }

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.Equal(secondTimestamp, timestamp);
    }

    [Fact]
    public void WriteTimestamp_DifferentProperties_HaveIndependentTimestamps()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstTimestamp = DateTimeOffset.UtcNow.AddDays(-100);
        var secondTimestamp = DateTimeOffset.UtcNow.AddDays(-50);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            person.LastName = "Doe";
        }

        // Assert
        Assert.Equal(firstTimestamp, person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
        Assert.Equal(secondTimestamp, person.GetPropertyReference("LastName").TryGetWriteTimestamp());
    }

    [Fact]
    public void WriteTimestamp_WithExplicitTimestamp_ChangeEventsShouldMatch()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        var timestamp = DateTimeOffset.UtcNow.AddDays(-200);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            person.FirstName = "Mother";
        }

        // Assert
        Assert.Equal(3, changes.Count); // backed, derived, derived
        Assert.True(changes.All(c => c.ChangedTimestamp == timestamp));
    }

    [Fact]
    public void WriteTimestamp_AfterScopeEnds_ShouldUseCurrentTime()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-200);

        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            person.FirstName = "First";
        }

        // Act
        var before = DateTimeOffset.UtcNow;
        person.LastName = "Second";

        // Assert
        var timestamp = person.GetPropertyReference("LastName").TryGetWriteTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
        Assert.NotEqual(explicitTimestamp, timestamp);
    }
}
