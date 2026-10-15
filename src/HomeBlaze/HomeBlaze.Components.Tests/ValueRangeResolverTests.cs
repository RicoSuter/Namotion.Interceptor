using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ValueRangeResolverTests
{
    // The initializer turns [State] into the metadata that holds the unit of a property.
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithLifecycle()
        .WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

    private RegisteredSubjectProperty GetProperty(string propertyName) =>
        new RangedSubject(_context).TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

    [Fact]
    public void WhenBothArgumentsAreSet_ThenTheyWinOverTheAttributes()
    {
        // Arrange
        var property = GetProperty(nameof(RangedSubject.Speed));

        // Act
        var isResolved = ValueRangeResolver.TryResolve(property, 100, 200, out var range, out var error);

        // Assert
        Assert.True(isResolved);
        Assert.Equal(new ValueRange(100, 200), range);
        Assert.Null(error);
    }

    [Fact]
    public void WhenNoArgumentIsSet_ThenTheNumericAttributesAreTheRange()
    {
        // Arrange
        var property = GetProperty(nameof(RangedSubject.Speed));

        // Act
        var isResolved = ValueRangeResolver.TryResolve(property, null, null, out var range, out _);

        // Assert
        Assert.True(isResolved);
        Assert.Equal(new ValueRange(0, 3000), range);
    }

    [Fact]
    public void WhenOneArgumentIsSet_ThenTheOtherEndComesFromItsAttribute()
    {
        // Arrange
        var property = GetProperty(nameof(RangedSubject.Speed));

        // Act
        var isResolved = ValueRangeResolver.TryResolve(property, 500, null, out var range, out _);

        // Assert
        Assert.True(isResolved);
        Assert.Equal(new ValueRange(500, 3000), range);
    }

    [Fact]
    public void WhenAnAttributeIsNoNumber_ThenItIsIgnored()
    {
        // Arrange
        var property = GetProperty(nameof(RangedSubject.Level));

        // Act
        var withoutMinimum = ValueRangeResolver.TryResolve(property, null, null, out _, out var error);
        var withMinimum = ValueRangeResolver.TryResolve(property, 2, null, out var range, out _);

        // Assert
        Assert.False(withoutMinimum);
        Assert.Equal(ValueRangeResolver.MissingRangeError, error);
        Assert.True(withMinimum);
        Assert.Equal(new ValueRange(2, 10), range);
    }

    [Fact]
    public void WhenThePropertyIsAPercentWithoutRange_ThenTheRangeIsZeroToOne()
    {
        // Arrange
        var property = GetProperty(nameof(RangedSubject.Load));

        // Act
        var isResolved = ValueRangeResolver.TryResolve(property, null, null, out var range, out _);

        // Assert
        Assert.True(isResolved);
        Assert.Equal(new ValueRange(0, 1), range);
    }

    [Fact]
    public void WhenNoRangeIsFound_ThenTheErrorNamesTheMissingRange()
    {
        // Arrange
        var property = GetProperty(nameof(RangedSubject.Plain));

        // Act
        var isResolved = ValueRangeResolver.TryResolve(property, null, 10, out _, out var error);

        // Assert
        Assert.False(isResolved);
        Assert.Equal("Needs a range: set Minimum and Maximum", error);
    }

    [Fact]
    public void WhenThereIsNoProperty_ThenOnlyTheArgumentsCount()
    {
        // Act
        var withArguments = ValueRangeResolver.TryResolve(null, 0, 10, out var range, out _);
        var withoutArguments = ValueRangeResolver.TryResolve(null, null, null, out _, out var error);

        // Assert
        Assert.True(withArguments);
        Assert.Equal(new ValueRange(0, 10), range);
        Assert.False(withoutArguments);
        Assert.Equal(ValueRangeResolver.MissingRangeError, error);
    }

    [Theory]
    [InlineData(5.0, 5.0, "Maximum must be above Minimum: 5 to 5")]
    [InlineData(0.8, 0.2, "Maximum must be above Minimum: 0.8 to 0.2")]
    [InlineData(double.NaN, 1.0, "Maximum must be above Minimum: NaN to 1")]
    [InlineData(0.0, double.PositiveInfinity, "Maximum must be above Minimum: 0 to Infinity")]
    public void WhenRangeIsEmptyOrNotFinite_ThenItIsAnError(double minimum, double maximum, string expected)
    {
        // Act
        var error = ValueRangeResolver.Validate(new ValueRange(minimum, maximum));

        // Assert
        Assert.Equal(expected, error);
    }

    [Fact]
    public void WhenMaximumIsAboveMinimum_ThenTheRangeHasNoError()
    {
        // Act
        var error = ValueRangeResolver.Validate(new ValueRange(-10, 0.5));

        // Assert
        Assert.Null(error);
    }
}
