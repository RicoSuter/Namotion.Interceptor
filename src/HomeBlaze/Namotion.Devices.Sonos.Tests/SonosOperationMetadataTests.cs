using System.Reflection;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosOperationMetadataTests
{
    private static readonly Regex NumericRange = new(@"-?\d+(\.\d+)?\s*(to|-|\.\.)\s*-?\d");

    public static TheoryData<string> Operations()
    {
        var data = new TheoryData<string>();
        foreach (var type in new[] { typeof(SonosPlayer), typeof(SonosHomeTheater), typeof(SonosGroup), typeof(SonosSystem) })
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

    [Theory]
    [MemberData(nameof(Operations))]
    public void WhenOperationIsListed_ThenItHasAnEnabledFlag(string operation)
    {
        // Arrange
        var method = GetMethod(operation);

        // The name an operation is registered under, which the flag's attribute must name to be found.
        var operationName = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name;

        // Act
        var flag = method.DeclaringType!.GetProperty($"{operationName}_IsEnabled", BindingFlags.Public | BindingFlags.Instance);

        // Assert
        Assert.True(flag is not null, $"{operation} has no {operationName}_IsEnabled property.");
        Assert.Equal(typeof(bool), flag.PropertyType);
        Assert.NotNull(flag.GetCustomAttribute<DerivedAttribute>());

        var attribute = flag.GetCustomAttribute<PropertyAttributeAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(operationName, attribute.PropertyName);
        Assert.Equal(KnownAttributes.IsEnabled, attribute.AttributeName);
    }

    [Fact]
    public void WhenPlaybackStateIsRegistered_ThenItsOwnDisplayPositionComesBeforeTheInterfaceOne()
    {
        // Arrange
        var system = TestFixtures.CreateGroupedSystem();
        IInterceptorSubject[] subjects = [system.Players[TestFixtures.OfficeUuid], system.Groups[TestFixtures.OfficeUuid]];
        var context = InterceptorSubjectContext.Create().WithRegistry();
        foreach (var subject in subjects)
        {
            subject.Context.AddFallbackContext(context);
        }

        // Act
        // The first position set wins when the state attributes of a property are merged, class before interface.
        var positions = subjects.Select(subject => subject
            .TryGetRegisteredProperty(nameof(IMediaPlaybackState.PlaybackState))!
            .ReflectionAttributes
            .OfType<StateAttribute>()
            .First(attribute => attribute.IsPositionSet)
            .Position).ToArray();

        // Assert
        Assert.Equal([10, 10], positions);
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
