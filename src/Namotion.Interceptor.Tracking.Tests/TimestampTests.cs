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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => handler);

        var mother = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangedObservable().Subscribe(c => changes.Add(c));
       
        var timestamp = DateTimeOffset.Now.AddDays(-200);

        // Act
        using (SubjectMutationContext.BeginTimestampScope(timestamp))
        {
            mother.FirstName = "Mother";
        }

        var currentTimestamp = SubjectMutationContext.GetCurrentTimestamp();
        
        // Assert
        Assert.Equal(3, changes.Count); // backed, derived, derived
        Assert.NotEqual(currentTimestamp, timestamp);
        Assert.True(changes.All(c => c.Timestamp == timestamp));

        mother.LastName = "Mu"; // should use now not timestamp
        Assert.NotEqual(timestamp, changes.Last().Timestamp);
    }
}