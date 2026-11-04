namespace Namotion.Interceptor.Tests;

public class PublishDerivedPropertyValueTests
{
    [Fact]
    public void WhenPropertyIsNotDerived_ThenPublishDerivedPropertyValueThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var car = new Car(context);
        var property = new PropertyReference(car, nameof(Car.Speed));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => property.PublishDerivedPropertyValue());
    }
}
