using HomeBlaze.Components.Controls;
using HomeBlaze.Components.Tests.Models;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SliderMathTests
{
    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(long?))]
    [InlineData(typeof(byte))]
    public void WhenPropertyHoldsWholeNumbers_ThenTheDefaultStepIsOne(Type propertyType)
    {
        // Act
        var step = SliderMath.GetDefaultStep(propertyType, new ValueRange(0, 3000));

        // Assert
        Assert.Equal(1, step);
    }

    [Theory]
    [InlineData(typeof(double))]
    [InlineData(typeof(decimal?))]
    public void WhenPropertyHoldsFractions_ThenTheDefaultStepIsAHundredthOfTheRange(Type propertyType)
    {
        // Act
        var step = SliderMath.GetDefaultStep(propertyType, new ValueRange(10, 30));

        // Assert
        Assert.Equal(0.2, step, 10);
    }

    [Theory]
    [InlineData(5.0, 5.0, "Maximum must be above Minimum: 5 to 5")]
    [InlineData(0.8, 0.2, "Maximum must be above Minimum: 0.8 to 0.2")]
    [InlineData(double.NaN, 1.0, "Maximum must be above Minimum: NaN to 1")]
    [InlineData(0.0, double.PositiveInfinity, "Maximum must be above Minimum: 0 to Infinity")]
    public void WhenRangeIsEmptyOrNotFinite_ThenItIsAnError(double minimum, double maximum, string expected)
    {
        // Act
        var error = SliderMath.ValidateRange(new ValueRange(minimum, maximum));

        // Assert
        Assert.Equal(expected, error);
    }

    [Fact]
    public void WhenMaximumIsAboveMinimum_ThenTheRangeHasNoError()
    {
        // Act
        var error = SliderMath.ValidateRange(new ValueRange(-10, 0.5));

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenStepHasAFractionForAWholeNumberProperty_ThenItIsAnError()
    {
        // Act
        var error = SliderMath.ValidateStep(typeof(int), 0.5);

        // Assert
        Assert.Equal("Step must be a whole number for a whole number property: 0.5", error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void WhenStepIsNotAboveZero_ThenItIsAnError(double step)
    {
        // Act
        var error = SliderMath.ValidateStep(typeof(double), step);

        // Assert
        Assert.NotNull(error);
        Assert.StartsWith("Step must be above 0: ", error);
    }

    [Fact]
    public void WhenStepFitsTheProperty_ThenThereIsNoError()
    {
        // Act
        var wholeNumber = SliderMath.ValidateStep(typeof(int), 100);
        var fraction = SliderMath.ValidateStep(typeof(double), 0.25);
        var none = SliderMath.ValidateStep(typeof(int), null);

        // Assert
        Assert.Null(wholeNumber);
        Assert.Null(fraction);
        Assert.Null(none);
    }

    [Theory]
    [InlineData(4.2, 5)]
    [InlineData(3.9, 3)]
    [InlineData(20, 11)]
    [InlineData(-5, 1)]
    public void WhenSliderStandsBetweenSteps_ThenTheValueIsTheNearestStepFromTheMinimumInsideTheRange(double sliderValue, double expected)
    {
        // Act
        var value = SliderMath.ToPropertyValue(sliderValue, new ValueRange(1, 11), 2, 1, typeof(double));

        // Assert
        Assert.Equal(expected, value);
    }

    [Fact]
    public void WhenStepIsAFraction_ThenTheValueKeepsNoBinaryRest()
    {
        // Act
        var value = SliderMath.ToPropertyValue(0.3, new ValueRange(0, 1), 0.1, 1, typeof(double));

        // Assert
        Assert.Equal(0.3, value);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.5, 2)]
    [InlineData(2.9, 3)]
    [InlineData(10.5, 10)]
    public void WhenRangeEndHasAFractionForAWholeNumberProperty_ThenTheValueIsRoundedHalfAwayFromZeroInsideTheRange(double sliderValue, double expected)
    {
        // Act
        var value = SliderMath.ToPropertyValue(sliderValue, new ValueRange(0.5, 10.5), 1, 1, typeof(int));

        // Assert
        Assert.Equal(expected, value);
    }

    [Fact]
    public void WhenRangeEndHasAFractionForANegativeWholeNumberProperty_ThenTheValueIsRoundedAwayFromZero()
    {
        // Act
        var value = SliderMath.ToPropertyValue(-2.5, new ValueRange(-10.5, 0), 1, 1, typeof(long?));

        // Assert
        Assert.Equal(-3, value);
    }

    [Fact]
    public void WhenRangeEndHasAFractionForAFractionProperty_ThenTheValueStaysOnTheStep()
    {
        // Act
        var value = SliderMath.ToPropertyValue(2.5, new ValueRange(0.5, 10.5), 1, 1, typeof(double));

        // Assert
        Assert.Equal(2.5, value);
    }

    [Theory]
    [InlineData(typeof(double))]
    [InlineData(typeof(decimal))]
    public void WhenRoundingPushesAboveAFractionalMaximum_ThenTheValueStepsBackInsideTheRange(Type propertyType)
    {
        // Act
        var value = SliderMath.ToPropertyValue(0.6, new ValueRange(0, 0.6), 1, 1, propertyType);

        // Assert
        Assert.Equal(0, value);
    }

    [Fact]
    public void WhenRoundingPushesBelowAFractionalMinimum_ThenTheValueSnapsToMinimum()
    {
        // Arrange
        var minimum = 1.0 / 3;

        // Act
        var value = SliderMath.ToPropertyValue(minimum, new ValueRange(minimum, 10), 1, 1, typeof(double));

        // Assert
        Assert.Equal(minimum, value);
    }

    [Fact]
    public void WhenRangeIsScaled_ThenTheEndsKeepNoBinaryRest()
    {
        // Act
        var scaled = SliderMath.ToSliderScale(0.29, 100);

        // Assert
        Assert.Equal(29, scaled);
    }

    [Fact]
    public void WhenPropertyIsAPercent_ThenTheSliderShowsHundredTimesTheValueAndWritesAFraction()
    {
        // Arrange
        var subject = new ControlTargetSubject(ControlTestContext.Create());
        var load = ControlTestContext.GetProperty(subject, nameof(ControlTargetSubject.Load));
        var range = new ValueRange(0, 1);

        // Act
        var scale = SliderMath.GetScale(load);
        var shown = SliderMath.ToSliderValue(0.51, range, scale);
        var written = SliderMath.ToPropertyValue(57, range, 0.01, scale, load.Type);

        // Assert
        Assert.Equal(100, scale);
        Assert.Equal(51, shown);
        Assert.Equal(0.57, written);
    }

    [Fact]
    public void WhenPropertyIsNoPercent_ThenTheScaleIsOne()
    {
        // Arrange
        var subject = new ControlTargetSubject(ControlTestContext.Create());
        var speed = ControlTestContext.GetProperty(subject, nameof(ControlTargetSubject.Speed));

        // Act
        var scale = SliderMath.GetScale(speed);
        var missingScale = SliderMath.GetScale(null);

        // Assert
        Assert.Equal(1, scale);
        Assert.Equal(1, missingScale);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text")]
    public void WhenValueIsMissingOrNoNumber_ThenTheSliderStandsAtTheMinimum(object? value)
    {
        // Act
        var shown = SliderMath.ToSliderValue(value, new ValueRange(0.2, 0.8), 100);

        // Assert
        Assert.Equal(20, shown);
    }

    [Fact]
    public void WhenValueIsANumber_ThenTheSliderStandsAtIt()
    {
        // Act
        var whole = SliderMath.ToSliderValue(1200, new ValueRange(0, 3000), 1);
        var fraction = SliderMath.ToSliderValue(12.5m, new ValueRange(0, 100), 1);

        // Assert
        Assert.Equal(1200, whole);
        Assert.Equal(12.5, fraction);
    }
}
