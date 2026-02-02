using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

public interface ITemperatureSensorInterface
{
    double TemperatureCelsius { get; set; }

    [Derived]
    double TemperatureFahrenheit => TemperatureCelsius * 9 / 5 + 32;

    bool IsFreezing => TemperatureCelsius <= 0;
}
