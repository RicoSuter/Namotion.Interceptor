using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangedHandlerTests
{
    [Fact]
    public void WhenPropertyIsChanged_ThenChangeHandlerIsTriggered()
    {
        // Arrange
        var changes = new List<PropertyChangedContext>();
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable();

        context
            .GetPropertyChangedObservable()
            .Subscribe(changes.Add);

        // Act
        var person = new Person(context);
        person.FirstName = "Rico";

        // Assert
        Assert.Contains(changes, c => 
            c.Property.Name == "FirstName" &&
            c.OldValue is null &&
            c.NewValue?.ToString() == "Rico");
    }
}