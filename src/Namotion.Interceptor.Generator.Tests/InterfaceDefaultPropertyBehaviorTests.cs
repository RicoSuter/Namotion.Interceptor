using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Generator.Tests;

public partial class InterfaceDefaultPropertyBehaviorTests
{
    #region InterfaceDefaultProperty_RegisteredAndWorks

    public interface ITemperatureSensorInterface
    {
        double TemperatureCelsius { get; set; }

        [Derived]
        double TemperatureFahrenheit => TemperatureCelsius * 9 / 5 + 32;

        bool IsFreezing => TemperatureCelsius <= 0;
    }

    [InterceptorSubject]
    public partial class SensorWithInterfaceDefaults : ITemperatureSensorInterface
    {
        public partial double TemperatureCelsius { get; set; }
    }

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
        var defaultProp = SubjectPropertyMetadataCache.Get<SensorWithInterfaceDefaults>()["TemperatureFahrenheit"];
        Assert.False(defaultProp.IsIntercepted);
        Assert.Null(defaultProp.SetValue);
    }

    #endregion

    #region NestedInterface_RegisteredAndWorks

    public partial class OuterClass
    {
        public interface INestedSensor
        {
            double Value { get; set; }

            string Status => Value > 0 ? "Active" : "Inactive";
        }
    }

    [InterceptorSubject]
    public partial class SensorWithNestedInterface : OuterClass.INestedSensor
    {
        public partial double Value { get; set; }
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

        var statusProp = SubjectPropertyMetadataCache.Get<SensorWithNestedInterface>()["Status"];
        Assert.False(statusProp.IsIntercepted);
        Assert.Equal("Active", statusProp.GetValue?.Invoke(sensor));
    }

    #endregion

    #region WritableInterfaceDefault_RegisteredAndWorks

    public interface IWritableDefaultInterface
    {
        double Temperature { get; set; }

        string Label { get => $"Temp: {Temperature}"; set { } }
    }

    [InterceptorSubject]
    public partial class SensorWithWritableDefault : IWritableDefaultInterface
    {
        public partial double Temperature { get; set; }
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

        var labelProp = SubjectPropertyMetadataCache.Get<SensorWithWritableDefault>()["Label"];
        Assert.False(labelProp.IsIntercepted);
        Assert.Equal("Temp: 25", labelProp.GetValue?.Invoke(sensor));
        Assert.Null(labelProp.SetValue);
    }

    #endregion

    #region InitOnlyInterface_RegisteredAndWorks

    public interface IInitOnlyInterface
    {
        string Id { get; init; }

        string DisplayId => $"ID: {Id}";
    }

    [InterceptorSubject]
    public partial class SensorWithInitOnly : IInitOnlyInterface
    {
        public partial string Id { get; init; }
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

        var displayIdProp = SubjectPropertyMetadataCache.Get<SensorWithInitOnly>()["DisplayId"];
        Assert.False(displayIdProp.IsIntercepted);
        Assert.Equal("ID: ABC123", displayIdProp.GetValue?.Invoke(sensor));
    }

    #endregion
}
