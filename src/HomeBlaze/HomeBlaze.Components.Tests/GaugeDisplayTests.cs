using System.Numerics;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services.Lifecycle;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class GaugeDisplayTests
{
    // The initializer turns [State] into the registry attribute that holds the unit, as in the application.
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithLifecycle()
        .WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

    private static ValueReading CreateReading(MeasuredSubject subject, string propertyName, Color color = Color.Default)
    {
        var property = subject.TryGetRegisteredProperty(propertyName)!;
        var value = property.GetValue();
        return new ValueReading(property, value, value?.ToString() ?? ValueReader.MissingText, color, null, null);
    }

    [Fact]
    public void WhenRangeIsGivenAsArguments_ThenPercentIsTheShareOfTheValue()
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Speed = 500 };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Speed));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, 0, 2000);

        // Assert
        Assert.Equal(25d, state.Percent);
        Assert.Equal(500d, state.AriaValue);
        Assert.Equal(new ValueRange(0, 2000), state.Range);
        Assert.Null(state.Error);
    }

    [Fact]
    public void WhenPropertyHasRangeAttributes_ThenTheyGiveTheRange()
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { TargetSpeed = 1500 };
        var reading = CreateReading(subject, nameof(MeasuredSubject.TargetSpeed));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, null, null);

        // Assert
        Assert.Equal(50d, state.Percent);
        Assert.Equal(new ValueRange(0, 3000), state.Range);
        Assert.Null(state.Error);
    }

    [Fact]
    public void WhenPropertyIsAPercentWithoutRange_ThenRangeIsZeroToOne()
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Load = 0.25 };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Load));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, null, null);

        // Assert
        Assert.Equal(25d, state.Percent);
        Assert.Equal(new ValueRange(0, 1), state.Range);
    }

    [Fact]
    public void WhenPropertyHasNoRange_ThenStateHasTheRangeError()
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Speed = 500 };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Speed));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, null, null);

        // Assert
        Assert.Equal("Needs a range: set Minimum and Maximum", state.Error);
        Assert.Null(state.Percent);
    }

    [Theory]
    [InlineData(100d, 100d, "Maximum must be above Minimum: 100 to 100")]
    [InlineData(200d, 100d, "Maximum must be above Minimum: 200 to 100")]
    public void WhenMaximumIsNotAboveMinimum_ThenStateHasTheRangeError(double minimum, double maximum, string expected)
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Speed = 500 };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Speed));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, minimum, maximum);

        // Assert
        Assert.Equal(expected, state.Error);
        Assert.Null(state.Percent);
        Assert.Null(state.Range);
    }

    [Theory]
    [InlineData(0.25, 0.25)]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    public void WhenValueIsShownAsAMeter_ThenItsAriaValueIsKeptInsideTheRange(double load, double expected)
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Load = load };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Load));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, null, null);

        // Assert
        Assert.Equal(expected, state.AriaValue);
    }

    [Fact]
    public void WhenPathLeadsNowhere_ThenStateHasNoErrorAndAnEmptyRing()
    {
        // Act
        var state = ValueRangeResolver.GetState(null, null, null, null);

        // Assert
        Assert.Null(state.Error);
        Assert.Null(state.Percent);
        Assert.Null(state.AriaValue);
        Assert.Null(state.Range);
    }

    [Fact]
    public void WhenPropertyHoldsNoNumberAndHasNoRange_ThenStateHasNoErrorAndAnEmptyRing()
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Name = "Pump" };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Name));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, null, null);

        // Assert
        Assert.Null(state.Error);
        Assert.Null(state.Percent);
        Assert.Null(state.Range);
    }

    [Fact]
    public void WhenPathLeadsNowhereAndTheRangeIsEmpty_ThenStateHasTheRangeError()
    {
        // Act
        var state = ValueRangeResolver.GetState(null, null, 10, 0);

        // Assert
        Assert.Equal("Maximum must be above Minimum: 10 to 0", state.Error);
    }

    [Fact]
    public void WhenValueIsNotSet_ThenRingIsEmptyAndRangeIsKept()
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Pressure = null };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Pressure));

        // Act
        var state = ValueRangeResolver.GetState(reading.Property, reading.Value, 0, 10);

        // Assert
        Assert.Null(state.Percent);
        Assert.Null(state.AriaValue);
        Assert.Equal(new ValueRange(0, 10), state.Range);
        Assert.Null(state.Error);
    }

    [Theory]
    [InlineData(Color.Default, Color.Primary)]
    [InlineData(Color.Success, Color.Success)]
    [InlineData(Color.Error, Color.Error)]
    public void WhenReadingHasTheColorOfACase_ThenRingHasItAndPrimaryWithoutOne(Color caseColor, Color expected)
    {
        // Arrange
        var subject = new MeasuredSubject(_context) { Speed = 500 };
        var reading = CreateReading(subject, nameof(MeasuredSubject.Speed), caseColor);

        // Act
        var color = GaugeDisplay.GetColor(reading);

        // Assert
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData(GaugeSize.Small, 72)]
    [InlineData(GaugeSize.Medium, 112)]
    [InlineData(GaugeSize.Large, 160)]
    public void WhenSizeIsChosen_ThenRingHasItsPixelSize(GaugeSize size, int expected)
    {
        // Act
        var pixels = GaugeDisplay.GetPixelSize(size);
        var style = GaugeDisplay.GetRingStyle(size);

        // Assert
        Assert.Equal(expected, pixels);
        Assert.Equal($"width: {expected}px; height: {expected}px;", style);
    }

    [Theory]
    [InlineData(1000d, "1000")]
    [InlineData(0.5d, "0.5")]
    [InlineData(null, null)]
    public void WhenNumberIsWrittenForAnAriaAttribute_ThenItHasNoCultureAndNullStaysNull(double? number, string? expected)
    {
        // Act
        var text = GaugeDisplay.FormatNumber(number);

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void WhenGaugeIsCreated_ThenItIsMediumAndItsPathOffersNumbers()
    {
        // Act
        var gauge = new Gauge(_context);
        var path = typeof(Gauge).GetProperty(nameof(Gauge.Path))!.GetCustomAttribute<SubjectPathAttribute>()!;
        var map = typeof(Gauge).GetProperty(nameof(Gauge.Map))!.GetCustomAttribute<SubjectPathAttribute>()!;

        // Assert
        Assert.Equal(GaugeSize.Medium, gauge.Size);
        Assert.Null(path.SubjectType);
        Assert.Equal(SubjectPathKind.Property, path.Kind);
        Assert.Equal([typeof(INumber<>)], path.ValueTypes);
        Assert.False(path.IsWritable);
        Assert.Equal(typeof(ValueMap), map.SubjectType);
    }
}
