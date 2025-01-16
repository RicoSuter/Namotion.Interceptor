using Namotion.Interception.Lifecycle;

namespace Namotion.Proxy.Tests.ChangeTracking;

public class ReadPropertyRecorderTests
{
    [Fact]
    public void WhenPropertyIsChanged_ThenItIsPartOfRecordedProperties()
    {
        // Arrange
        var context = InterceptorContext
            .CreateBuilder()
            .WithPropertyChangedObservable()
            .WithReadPropertyRecorder()
            .Build();

        // Act
        var person = new Person(context);

        var recorder = context.StartRecordingPropertyReadCalls();
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