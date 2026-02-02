namespace Namotion.Interceptor.Generator.Tests.Models;

public interface IInitOnlyInterface
{
    string Id { get; init; }

    string DisplayId => $"ID: {Id}";
}
