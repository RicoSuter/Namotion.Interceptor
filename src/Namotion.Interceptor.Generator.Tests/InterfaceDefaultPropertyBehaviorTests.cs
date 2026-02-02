using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Generator.Tests;

public class InterfaceDefaultPropertyBehaviorTests
{
    [Fact]
    public void InterfaceDefaultProperty_AppearsInDefaultProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInterfaceDefaults(context);

        // Act
        var registeredSubject = sensor.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("TemperatureCelsius", propertyNames);
        Assert.Contains("TemperatureFahrenheit", propertyNames);
        Assert.Contains("IsFreezing", propertyNames);
    }

    [Fact]
    public void InterfaceDefaultProperty_GetterWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInterfaceDefaults(context) { TemperatureCelsius = 25.0 };

        // Act
        var registeredSubject = sensor.TryGetRegisteredSubject();
        var fahrenheitProp = registeredSubject?.TryGetProperty("TemperatureFahrenheit");

        // Assert
        Assert.NotNull(fahrenheitProp);
        Assert.Equal(77.0, fahrenheitProp.GetValue());
    }

    [Fact]
    public void InterfaceDefaultProperty_IsNotIntercepted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInterfaceDefaults(context);

        // Act
        var fahrenheitProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];

        // Assert
        Assert.False(fahrenheitProp.IsIntercepted);
        Assert.False(fahrenheitProp.IsDynamic);
    }

    [Fact]
    public void InterfaceDefaultProperty_SetterIsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInterfaceDefaults(context);

        // Act
        var fahrenheitProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];

        // Assert
        Assert.Null(fahrenheitProp.SetValue);
    }

    [Fact]
    public void NestedInterface_PropertiesRegisteredCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithNestedInterface(context) { Value = 42.0 };

        // Act
        var registeredSubject = sensor.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Value", propertyNames);
        Assert.Contains("Status", propertyNames);

        // Verify the default property works
        var statusProp = SensorWithNestedInterface.DefaultProperties["Status"];
        Assert.False(statusProp.IsIntercepted);
        Assert.False(statusProp.IsDynamic);
        Assert.Equal("Active", statusProp.GetValue!(sensor));
    }

    [Fact]
    public void WritableInterfaceDefault_PropertiesRegisteredCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithWritableDefault(context) { Temperature = 25.0 };

        // Act
        var registeredSubject = sensor.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Temperature", propertyNames);
        Assert.Contains("Label", propertyNames);

        // Verify the default property (setter is null because interface defaults have no backing field)
        var labelProp = SensorWithWritableDefault.DefaultProperties["Label"];
        Assert.False(labelProp.IsIntercepted);
        Assert.False(labelProp.IsDynamic);
        Assert.Equal("Temp: 25", labelProp.GetValue!(sensor));
        Assert.Null(labelProp.SetValue); // No setter - interface defaults are read-only in generated code
    }

    [Fact]
    public void InitOnlyInterface_PropertiesRegisteredCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInitOnly(context) { Id = "ABC123" };

        // Act
        var registeredSubject = sensor.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Id", propertyNames);
        Assert.Contains("DisplayId", propertyNames);

        // Verify the default property works
        var displayIdProp = SensorWithInitOnly.DefaultProperties["DisplayId"];
        Assert.False(displayIdProp.IsIntercepted);
        Assert.False(displayIdProp.IsDynamic);
        Assert.Equal("ID: ABC123", displayIdProp.GetValue!(sensor));
    }
}
