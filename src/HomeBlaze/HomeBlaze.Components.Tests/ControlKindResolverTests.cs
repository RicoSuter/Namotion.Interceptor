using HomeBlaze.Components.Controls;
using HomeBlaze.Components.Tests.Models;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ControlKindResolverTests
{
    private readonly ControlTargetSubject _subject = new(ControlTestContext.Create());

    [Theory]
    [InlineData(nameof(ControlTargetSubject.Start), ControlKind.OperationButton)]
    [InlineData(nameof(ControlTargetSubject.Count), ControlKind.OperationButton)]
    [InlineData(nameof(ControlTargetSubject.IsOn), ControlKind.Switch)]
    [InlineData(nameof(ControlTargetSubject.IsOpen), ControlKind.Switch)]
    [InlineData(nameof(ControlTargetSubject.IsReady), ControlKind.Indicator)]
    [InlineData(nameof(ControlTargetSubject.Mode), ControlKind.Select)]
    [InlineData(nameof(ControlTargetSubject.OptionalMode), ControlKind.Select)]
    [InlineData(nameof(ControlTargetSubject.FixedMode), ControlKind.Value)]
    [InlineData(nameof(ControlTargetSubject.Features), ControlKind.Value)]
    [InlineData(nameof(ControlTargetSubject.Speed), ControlKind.Slider)]
    [InlineData(nameof(ControlTargetSubject.Load), ControlKind.Slider)]
    [InlineData(nameof(ControlTargetSubject.Temperature), ControlKind.Value)]
    [InlineData(nameof(ControlTargetSubject.Name), ControlKind.Value)]
    public void WhenControlTargetsAPropertyOrAMethod_ThenItRendersTheWidgetForItsKind(string propertyName, ControlKind expected)
    {
        // Arrange
        var property = ControlTestContext.GetProperty(_subject, propertyName);

        // Act
        var kind = ControlKindResolver.Resolve(property, null, null);

        // Assert
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void WhenANumberWithoutRangeGetsARangeFromTheArguments_ThenItIsASlider()
    {
        // Arrange
        var property = ControlTestContext.GetProperty(_subject, nameof(ControlTargetSubject.Temperature));

        // Act
        var kind = ControlKindResolver.Resolve(property, 0, 50);

        // Assert
        Assert.Equal(ControlKind.Slider, kind);
    }

    [Fact]
    public void WhenThePathLeadsNowhere_ThenItIsAValueRow()
    {
        // Act
        var kind = ControlKindResolver.Resolve(null, 0, 50);

        // Assert
        Assert.Equal(ControlKind.Value, kind);
    }
}
