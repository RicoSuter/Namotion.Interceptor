using Namotion.Interceptor.OpcUa.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Every assertion here is independent of the machine's time zone, which is what makes them a guard
/// rather than a pin: the plain conversion this helper replaces only throws in some zones.
/// </summary>
public class OpcUaTimestampExtensionsTests
{
    [Fact]
    public void WhenTimestampIsABoundaryValue_ThenItBecomesTheMatchingBoundaryOffset()
    {
        // Arrange & Act
        var oldest = DateTime.MinValue.ToUtcDateTimeOffset();
        var newest = DateTime.MaxValue.ToUtcDateTimeOffset();

        // Assert
        Assert.Equal(DateTimeOffset.MinValue, oldest);
        Assert.Equal(DateTimeOffset.MaxValue, newest);
    }

    [Fact]
    public void WhenTimestampIsUtc_ThenTheInstantIsPreserved()
    {
        // Arrange
        var timestamp = new DateTime(2026, 7, 29, 12, 34, 56, DateTimeKind.Utc);

        // Act
        var converted = timestamp.ToUtcDateTimeOffset();

        // Assert
        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(timestamp, converted.UtcDateTime);
    }

    [Fact]
    public void WhenTimestampKindIsUnspecified_ThenItIsReadAsUtc()
    {
        // Arrange
        var timestamp = new DateTime(2026, 7, 29, 12, 34, 56);

        // Act
        var converted = timestamp.ToUtcDateTimeOffset();

        // Assert
        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(timestamp, converted.UtcDateTime);
    }
}
