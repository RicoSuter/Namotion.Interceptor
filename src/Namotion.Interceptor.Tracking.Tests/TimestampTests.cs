using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class TimestampTests
{
    [Fact]
    public void WhenDefiningAsyncLocalTimestamp_ThenAllChangesHaveThisTimestamp()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => handler);

        var mother = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));
       
        var timestamp = DateTimeOffset.Now.AddDays(-200);

        // Act
        SubjectMutationContext.ApplyChangesWithChangedTimestamp(timestamp, () =>
        {
            mother.FirstName = "Mother";
        });

        var currentTimestamp = SubjectMutationContext.GetChangedTimestamp();
        
        // Assert
        Assert.Equal(3, changes.Count); // backed, derived, derived
        Assert.NotEqual(currentTimestamp, timestamp);
        Assert.True(changes.All(c => c.ChangedTimestamp == timestamp));

        mother.LastName = "Mu"; // should use now not timestamp
        Assert.NotEqual(timestamp, changes.Last().ChangedTimestamp);
    }
}