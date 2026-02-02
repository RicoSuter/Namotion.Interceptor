using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Generator.Tests;

public class InterfaceDefaultPropertyBehaviorTests
{
    [Fact]
    public void InterfaceDefaultProperty_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInterfaceDefaults(context) { TemperatureCelsius = 25.0 };

        // Assert: properties appear in registry
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("TemperatureCelsius", propertyNames);
        Assert.Contains("TemperatureFahrenheit", propertyNames);
        Assert.Contains("IsFreezing", propertyNames);

        // Assert: getter works
        var fahrenheitProp = registeredSubject.TryGetProperty("TemperatureFahrenheit");
        Assert.NotNull(fahrenheitProp);
        Assert.Equal(77.0, fahrenheitProp.GetValue());

        // Assert: default property metadata
        var defaultProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];
        Assert.False(defaultProp.IsIntercepted);
        Assert.Null(defaultProp.SetValue);
    }

    [Fact]
    public void NestedInterface_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithNestedInterface(context) { Value = 42.0 };

        // Assert
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Value", propertyNames);
        Assert.Contains("Status", propertyNames);

        var statusProp = SensorWithNestedInterface.DefaultProperties["Status"];
        Assert.False(statusProp.IsIntercepted);
        Assert.Equal("Active", statusProp.GetValue!(sensor));
    }

    [Fact]
    public void WritableInterfaceDefault_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithWritableDefault(context) { Temperature = 25.0 };

        // Assert
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Temperature", propertyNames);
        Assert.Contains("Label", propertyNames);

        var labelProp = SensorWithWritableDefault.DefaultProperties["Label"];
        Assert.False(labelProp.IsIntercepted);
        Assert.Equal("Temp: 25", labelProp.GetValue!(sensor));
        Assert.Null(labelProp.SetValue);
    }

    [Fact]
    public void InitOnlyInterface_RegisteredAndWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new SensorWithInitOnly(context) { Id = "ABC123" };

        // Assert
        var registeredSubject = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Id", propertyNames);
        Assert.Contains("DisplayId", propertyNames);

        var displayIdProp = SensorWithInitOnly.DefaultProperties["DisplayId"];
        Assert.False(displayIdProp.IsIntercepted);
        Assert.Equal("ID: ABC123", displayIdProp.GetValue!(sensor));
    }
}
