using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class SubjectPropertyChangeWithSourceTests
{
    [Fact]
    public void WhenWithSourceIsCalledOnStringChange_ThenSourceIsReplacedAndEverythingElsePreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var changedTimestamp = DateTimeOffset.UtcNow;
        var receivedTimestamp = changedTimestamp.AddMilliseconds(5);
        var change = SubjectPropertyChange.Create(
            property, source: null, changedTimestamp, receivedTimestamp, "Old", "New");
        var source = new object();

        // Act
        var marked = change.WithSource(source);

        // Assert
        Assert.Same(source, marked.Source);
        Assert.Equal(property, marked.Property);
        Assert.Equal(changedTimestamp, marked.ChangedTimestamp);
        Assert.Equal(receivedTimestamp, marked.ReceivedTimestamp);
        Assert.Equal("Old", marked.GetOldValue<string>());
        Assert.Equal("New", marked.GetNewValue<string>());
    }

    [Fact]
    public void WhenWithSourceIsCalledOnInlineValueChange_ThenValuesArePreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var change = SubjectPropertyChange.Create(
            property, source: null, DateTimeOffset.UtcNow, receivedTimestamp: null, 1, 2);
        var source = new object();

        // Act
        var marked = change.WithSource(source);

        // Assert
        Assert.Same(source, marked.Source);
        Assert.Null(marked.ReceivedTimestamp);
        Assert.Equal(1, marked.GetOldValue<int>());
        Assert.Equal(2, marked.GetNewValue<int>());
    }
}
