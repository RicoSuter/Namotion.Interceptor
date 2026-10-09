using System.Reflection;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosOperationMetadataTests
{
    private static readonly Regex NumericRange = new(@"-?\d+(\.\d+)?\s*(to|-|\.\.)\s*-?\d");

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
        var method = GetMethod(operation);

        // Act
        var description = method.GetCustomAttribute<OperationAttribute>()!.Description;

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(description), $"{operation} has no description.");
        Assert.EndsWith(".", description);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void WhenOperationIsListed_ThenItHasATitle(string operation)
    {
        // Arrange
        var method = GetMethod(operation);

        // Act
        var title = method.GetCustomAttribute<OperationAttribute>()!.Title;

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(title), $"{operation} has no title.");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void WhenOperationTakesAPercent_ThenItsDescriptionStatesNoNumericRange(string operation)
    {
        // Arrange
        var method = GetMethod(operation);
        var takesPercent = method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<OperationParameterAttribute>()?.Unit == StateUnit.Percent);

        // Act
        var description = method.GetCustomAttribute<OperationAttribute>()!.Description!;

        // Assert
        // The unit carries the scale: the dialog shows percent, agents get a fraction hint.
        Assert.True(!takesPercent || !NumericRange.IsMatch(description), $"{operation} states a numeric range: {description}");
    }

    [Fact]
    public void WhenDeviceBaseTypeIsInspected_ThenItIsAbstract()
    {
        // Act
        var isAbstract = typeof(SonosDevice).IsAbstract;

        // Assert
        Assert.True(isAbstract);
    }

    private static MethodInfo GetMethod(string operation)
    {
        var separator = operation.IndexOf('.');
        var type = typeof(SonosSystem).Assembly.GetType($"Namotion.Devices.Sonos.{operation[..separator]}")!;
        return type.GetMethod(operation[(separator + 1)..])!;
    }
}
