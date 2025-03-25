using Namotion.Interceptor.Tracking.Recorder;
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

        var recorder = context
            .GetService<ReadPropertyRecorder>()
            .StartPropertyAccessRecording();
        
        using (recorder)
        {
             var firstName = person.FirstName;
        }

        var lastName = person.LastName;
        
        // TODO: Check whether recording also works with additional registered properties or attributes (registry)

        var allProperties = recorder.Properties;
        var properties = recorder.GetPropertiesAndReset();
        var propertiesAgain = recorder.GetPropertiesAndDispose();
        
        // Assert
        Assert.Single(properties);
        Assert.Single(allProperties);
        Assert.Empty(propertiesAgain);
        Assert.Contains(allProperties, p => p.Name == "FirstName");
    }
}