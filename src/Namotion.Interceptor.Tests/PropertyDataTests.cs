namespace Namotion.Interceptor.Tests;

public class PropertyDataTests
{
    [Fact]
    public void WhenKeyIsAbsent_ThenTryAddPropertyDataStoresValueAndReturnsTrue()
    {
        // Arrange
        var car = new Car();
        var property = new PropertyReference(car, nameof(Car.Speed));

        // Act
        var added = property.TryAddPropertyData("test.key", 1);

        // Assert
        Assert.True(added);
        Assert.True(property.TryGetPropertyData("test.key", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void WhenKeyIsPresent_ThenTryAddPropertyDataLeavesValueAndReturnsFalse()
    {
        // Arrange
        var car = new Car();
        var property = new PropertyReference(car, nameof(Car.Speed));
        property.TryAddPropertyData("test.key", 1);

        // Act
        var added = property.TryAddPropertyData("test.key", 2);

        // Assert
        Assert.False(added);
        Assert.True(property.TryGetPropertyData("test.key", out var value));
        Assert.Equal(1, value);
    }
}
