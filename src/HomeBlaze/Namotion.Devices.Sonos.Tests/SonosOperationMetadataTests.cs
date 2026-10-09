using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosOperationMetadataTests
{
    public static TheoryData<string> Operations()
    {
        var data = new TheoryData<string>();
        foreach (var type in new[] { typeof(SonosPlayer), typeof(SonosGroup), typeof(SonosSystem) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<OperationAttribute>() is not null)
                {
                    data.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void WhenOperationIsListed_ThenItHasADescription(string operation)
    {
        // Arrange
        var separator = operation.IndexOf('.');
        var type = typeof(SonosSystem).Assembly.GetType($"Namotion.Devices.Sonos.{operation[..separator]}")!;
        var method = type.GetMethod(operation[(separator + 1)..])!;

        // Act
        var description = method.GetCustomAttribute<OperationAttribute>()!.Description;

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(description), $"{operation} has no description.");
        Assert.EndsWith(".", description);
    }
}
