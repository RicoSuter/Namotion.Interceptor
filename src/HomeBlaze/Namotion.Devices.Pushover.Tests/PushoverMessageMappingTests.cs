using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Pushover.Client;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverMessageMappingTests
{
    private static PushoverMessageRequest Map(Notification notification) =>
        PushoverMessageMapping.Create("app", "user", notification, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), NullLogger.Instance);

    [Theory]
    [InlineData(NotificationPriority.Low, -1)]
    [InlineData(NotificationPriority.Normal, 0)]
    [InlineData(NotificationPriority.High, 1)]
    [InlineData(NotificationPriority.Emergency, 2)]
    public void WhenMappingPriority_ThenUsesThePushoverLevel(NotificationPriority priority, int expected)
    {
        // Act
        var result = PushoverMessageMapping.ToPushoverPriority(priority);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenNotificationIsNormal_ThenPriorityRetryAndExpireAreOmitted()
    {
        // Act
        var request = Map(new Notification { Message = "Hello", Title = "Title", Url = "https://example.com", UrlTitle = "Open" });

        // Assert
        Assert.Equal("Hello", request.Message);
        Assert.Equal("Title", request.Title);
        Assert.Equal("https://example.com", request.Url);
        Assert.Equal("Open", request.UrlTitle);
        Assert.Null(request.Priority);
        Assert.Null(request.Retry);
        Assert.Null(request.Expire);
    }

    [Fact]
    public void WhenNotificationIsEmergency_ThenRetryAndExpireAreSet()
    {
        // Act
        var request = Map(new Notification { Message = "Fire", Priority = NotificationPriority.Emergency });

        // Assert
        Assert.Equal(2, request.Priority);
        Assert.Equal(60, request.Retry);
        Assert.Equal(3600, request.Expire);
    }

    [Fact]
    public void WhenMessageIsTooLong_ThenItIsShortenedWithAnEllipsis()
    {
        // Act
        var request = Map(new Notification { Message = new string('a', 2000), Title = new string('b', 300) });

        // Assert
        Assert.Equal(PushoverMessageMapping.MaximumMessageLength, request.Message.Length);
        Assert.EndsWith("…", request.Message);
        Assert.Equal(PushoverMessageMapping.MaximumTitleLength, request.Title!.Length);
    }

    [Fact]
    public void WhenShorteningWouldSplitASurrogatePair_ThenThePairIsDropped()
    {
        // Arrange
        var value = new string('a', 8) + "😀" + "bbbb";

        // Act
        var shortened = PushoverMessageMapping.Shorten(value, 10, "message", NullLogger.Instance);

        // Assert
        Assert.Equal(new string('a', 8) + "…", shortened);
    }
}
