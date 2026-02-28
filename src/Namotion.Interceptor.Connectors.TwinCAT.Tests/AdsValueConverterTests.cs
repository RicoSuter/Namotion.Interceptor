using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests;

public class AdsValueConverterTests
{
    private readonly AdsValueConverter _converter = new();

    private RegisteredSubjectProperty GetProperty(string propertyName)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        return registeredSubject.Properties.First(p => p.Name == propertyName);
    }

    [Fact]
    public void ConvertToPropertyValue_WithNull_ReturnsNull()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Temperature));

        // Act
        var result = _converter.ConvertToPropertyValue(null, property);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToAdsValue_WithNull_ReturnsNull()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Temperature));

        // Act
        var result = _converter.ConvertToAdsValue(null, property);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToPropertyValue_WithDateTimeForDateTimeOffsetProperty_ReturnsDateTimeOffset()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Timestamp));
        var dateTime = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);

        // Act
        var result = _converter.ConvertToPropertyValue(dateTime, property);

        // Assert
        var dateTimeOffset = Assert.IsType<DateTimeOffset>(result);
        Assert.Equal(new DateTimeOffset(dateTime, TimeSpan.Zero), dateTimeOffset);
    }

    [Fact]
    public void ConvertToAdsValue_WithDateTimeOffset_ReturnsUtcDateTime()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Timestamp));
        var offset = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.FromHours(2));

        // Act
        var result = _converter.ConvertToAdsValue(offset, property);

        // Assert
        var dateTime = Assert.IsType<DateTime>(result);
        Assert.Equal(offset.UtcDateTime, dateTime);
    }

    [Fact]
    public void ConvertToPropertyValue_WithLocalDateTime_ReturnsUtcDateTimeOffset()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Timestamp));
        var dateTime = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Local);

        // Act
        var result = _converter.ConvertToPropertyValue(dateTime, property);

        // Assert — SpecifyKind(Utc) preserves raw ticks as UTC
        var dateTimeOffset = Assert.IsType<DateTimeOffset>(result);
        Assert.Equal(TimeSpan.Zero, dateTimeOffset.Offset);
        Assert.Equal(dateTime.Ticks, dateTimeOffset.Ticks);
    }

    [Fact]
    public void ConvertToPropertyValue_WithUnspecifiedDateTime_ReturnsUtcDateTimeOffset()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Timestamp));
        var dateTime = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Unspecified);

        // Act
        var result = _converter.ConvertToPropertyValue(dateTime, property);

        // Assert — SpecifyKind(Utc) preserves raw ticks as UTC
        var dateTimeOffset = Assert.IsType<DateTimeOffset>(result);
        Assert.Equal(TimeSpan.Zero, dateTimeOffset.Offset);
        Assert.Equal(dateTime.Ticks, dateTimeOffset.Ticks);
    }

    [Fact]
    public void ConvertToPropertyValue_WithInt_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Counter));

        // Act
        var result = _converter.ConvertToPropertyValue(42, property);

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ConvertToAdsValue_WithInt_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Counter));

        // Act
        var result = _converter.ConvertToAdsValue(42, property);

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ConvertToPropertyValue_WithString_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Name));

        // Act
        var result = _converter.ConvertToPropertyValue("hello", property);

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ConvertToAdsValue_WithString_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Name));

        // Act
        var result = _converter.ConvertToAdsValue("hello", property);

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ConvertToPropertyValue_WithFloat_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Pressure));

        // Act
        var result = _converter.ConvertToPropertyValue(3.14f, property);

        // Assert
        Assert.Equal(3.14f, result);
    }

    [Fact]
    public void ConvertToPropertyValue_WithArray_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Values));
        var values = new[] { 1, 2, 3 };

        // Act
        var result = _converter.ConvertToPropertyValue(values, property);

        // Assert
        Assert.Same(values, result);
    }

    [Fact]
    public void ConvertToAdsValue_WithArray_PassesThrough()
    {
        // Arrange
        var property = GetProperty(nameof(TestPlcModel.Values));
        var values = new[] { 1, 2, 3 };

        // Act
        var result = _converter.ConvertToAdsValue(values, property);

        // Assert
        Assert.Same(values, result);
    }

    [Fact]
    public void CustomSubclass_CanOverrideConversion()
    {
        // Arrange
        var converter = new ScalingValueConverter(scaleFactor: 10.0);
        var property = GetProperty(nameof(TestPlcModel.Temperature));

        // Act
        var toProperty = converter.ConvertToPropertyValue(5.0, property);
        var toAds = converter.ConvertToAdsValue(50.0, property);

        // Assert
        Assert.Equal(50.0, toProperty);
        Assert.Equal(5.0, toAds);
    }

    private class ScalingValueConverter(double scaleFactor) : AdsValueConverter
    {
        public override object? ConvertToPropertyValue(object? adsValue, RegisteredSubjectProperty property)
        {
            if (adsValue is double value)
                return value * scaleFactor;

            return base.ConvertToPropertyValue(adsValue, property);
        }

        public override object? ConvertToAdsValue(object? propertyValue, RegisteredSubjectProperty property)
        {
            if (propertyValue is double value)
                return value / scaleFactor;

            return base.ConvertToAdsValue(propertyValue, property);
        }
    }
}
