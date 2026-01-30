using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Tests;

public partial class InterfaceAttributeInheritanceTests
{
    [AttributeUsage(AttributeTargets.Property)]
    private class UnitAttribute : Attribute, ISubjectPropertyInitializer
    {
        public string Unit { get; }
        public UnitAttribute(string unit) => Unit = unit;

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            property.AddAttribute("Unit", typeof(string), _ => Unit, null);
        }
    }

    private interface ITemperatureSensor
    {
        [Unit("°C")]
        double Temperature { get; }
    }

    [InterceptorSubject]
    private partial class TemperatureSensor : ITemperatureSensor
    {
        public partial double Temperature { get; set; }
    }

    [Fact]
    public void InterfaceAttribute_WithInitializer_AddsPropertyAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var sensor = new TemperatureSensor(context);

        // Act
        var registeredProperty = sensor.TryGetRegisteredSubject()?.TryGetProperty("Temperature");
        var unitAttribute = registeredProperty?.TryGetAttribute("Unit");

        // Assert
        Assert.NotNull(unitAttribute);
        Assert.Equal("°C", unitAttribute.GetValue());
    }
}
