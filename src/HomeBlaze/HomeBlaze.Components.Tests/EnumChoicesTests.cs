using HomeBlaze.Components.Controls;
using HomeBlaze.Components.Tests.Models;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class EnumChoicesTests
{
    [Theory]
    [InlineData(typeof(ControlTargetMode))]
    [InlineData(typeof(ControlTargetMode?))]
    public void WhenPropertyHoldsAnEnum_ThenItsEnumTypeIsFound(Type propertyType)
    {
        // Act
        var enumType = EnumChoices.GetEnumType(propertyType);

        // Assert
        Assert.Equal(typeof(ControlTargetMode), enumType);
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(bool?))]
    public void WhenPropertyHoldsNoEnum_ThenThereIsNoEnumType(Type propertyType)
    {
        // Act
        var enumType = EnumChoices.GetEnumType(propertyType);

        // Assert
        Assert.Null(enumType);
    }

    [Fact]
    public void WhenEnumCombinesAsFlags_ThenItIsAFlagsEnumWithAnErrorThatNamesIt()
    {
        // Act
        var isFlags = EnumChoices.IsFlags(typeof(ControlTargetFeatures));
        var isModeFlags = EnumChoices.IsFlags(typeof(ControlTargetMode));
        var error = EnumChoices.GetFlagsError(typeof(ControlTargetFeatures));

        // Assert
        Assert.True(isFlags);
        Assert.False(isModeFlags);
        Assert.Equal("Cannot select a value of the flags enum ControlTargetFeatures", error);
    }
}
