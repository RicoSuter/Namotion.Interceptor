using Namotion.Interceptor.Generator.Tests.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class InterfaceDefaultPropertyBehaviorTests
{
    [Fact]
    public void InterfaceDefaultProperty_AppearsInDefaultProperties()
    {
        // Arrange & Act
        var properties = SensorWithInterfaceDefaults.DefaultProperties;

        // Assert
        Assert.True(properties.ContainsKey("TemperatureFahrenheit"));
        Assert.True(properties.ContainsKey("IsFreezing"));
    }

    [Fact]
    public void InterfaceDefaultProperty_GetterWorks()
    {
        // Arrange
        var sensor = new SensorWithInterfaceDefaults { TemperatureCelsius = 25.0 };
        var properties = SensorWithInterfaceDefaults.DefaultProperties;

        // Act
        var fahrenheitProp = properties["TemperatureFahrenheit"];
        var value = fahrenheitProp.GetValue!(sensor);

        // Assert
        Assert.Equal(77.0, value);
    }

    [Fact]
    public void InterfaceDefaultProperty_IsNotIntercepted()
    {
        // Arrange & Act
        var fahrenheitProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];

        // Assert
        Assert.False(fahrenheitProp.IsIntercepted);
    }

    [Fact]
    public void InterfaceDefaultProperty_SetterIsNull()
    {
        // Arrange & Act
        var fahrenheitProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];

        // Assert
        Assert.Null(fahrenheitProp.SetValue);
    }
}
