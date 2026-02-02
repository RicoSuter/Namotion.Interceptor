namespace Namotion.Interceptor.Generator.Tests.Models;

public interface IWritableDefaultInterface
{
    double Temperature { get; set; }

    string Label { get => $"Temp: {Temperature}"; set { } }
}
