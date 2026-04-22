using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ParameterMarkerAttribute : Attribute
{
    public string Label { get; }

    public ParameterMarkerAttribute(string label) => Label = label;
}

[InterceptorSubject]
public partial class ParameterAttributeHost
{
    [SubjectMethod]
    public void Configure([ParameterMarker("first")] int value) { }

    [SubjectMethod]
    public void ConfigureMany(
        [ParameterMarker("alpha")] int first,
        int second,
        [ParameterMarker("gamma")] string third)
    { }
}

public class SubjectMethodParameterAttributeTests
{
    [Fact]
    public void WhenParameterHasCustomAttribute_ThenAttributeIsExposedInMetadata()
    {
        // Arrange
        var host = new ParameterAttributeHost();
        var configureMethod = ((IInterceptorSubject)host).Methods["Configure"];

        // Act
        var parameter = configureMethod.Parameters[0];
        var marker = parameter.Attributes.OfType<ParameterMarkerAttribute>().FirstOrDefault();

        // Assert
        Assert.NotNull(marker);
        Assert.Equal("first", marker.Label);
    }

    [Fact]
    public void WhenMethodHasMultipleParameters_ThenEachParameterExposesItsOwnAttributes()
    {
        // Arrange
        var host = new ParameterAttributeHost();
        var configureManyMethod = ((IInterceptorSubject)host).Methods["ConfigureMany"];

        // Act
        var firstMarker = configureManyMethod.Parameters[0].Attributes.OfType<ParameterMarkerAttribute>().FirstOrDefault();
        var secondMarker = configureManyMethod.Parameters[1].Attributes.OfType<ParameterMarkerAttribute>().FirstOrDefault();
        var thirdMarker = configureManyMethod.Parameters[2].Attributes.OfType<ParameterMarkerAttribute>().FirstOrDefault();

        // Assert
        Assert.NotNull(firstMarker);
        Assert.Equal("alpha", firstMarker.Label);
        Assert.Null(secondMarker);
        Assert.NotNull(thirdMarker);
        Assert.Equal("gamma", thirdMarker.Label);
    }
}
