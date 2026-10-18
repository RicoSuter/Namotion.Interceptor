using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Pushover.Client;
using Namotion.Devices.Pushover.Tests.Testing;
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
    public void WhenPriorityIsNotADefinedMember_ThenMappingThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => PushoverMessageMapping.ToPushoverPriority((NotificationPriority)7));
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
        var request = Map(PushoverTestFactory.EmergencyNotification);

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

    [Fact]
    public void WhenMaximumLengthIsOne_ThenTheResultIsJustTheEllipsisAndDoesNotThrow()
    {
        // Act
        var shortened = PushoverMessageMapping.Shorten("abcdef", 1, "message", NullLogger.Instance);

        // Assert
        Assert.Equal("…", shortened);
    }

    [Fact]
    public void WhenMaximumLengthIsZero_ThenTheResultIsEmptyAndDoesNotThrow()
    {
        // Act
        var shortened = PushoverMessageMapping.Shorten("abcdef", 0, "message", NullLogger.Instance);

        // Assert
        Assert.Equal(string.Empty, shortened);
    }

    [Fact]
    public void WhenUrlIsTooLong_ThenItAndItsUrlTitleAreOmittedInsteadOfBeingShortened()
    {
        // Act
        var request = Map(new Notification { Message = "Hello", Url = "https://example.com/" + new string('a', 600), UrlTitle = "Open" });

        // Assert
        Assert.Null(request.Url);
        Assert.Null(request.UrlTitle);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/Devices/Kitchen")]
    [InlineData("example.com")]
    [InlineData("not a url")]
    public void WhenUrlIsNotAnAbsoluteHttpOrHttpsAddress_ThenItAndItsUrlTitleAreOmitted(string url)
    {
        // Act
        var request = Map(new Notification { Message = "Hello", Url = url, UrlTitle = "Open" });

        // Assert
        Assert.Null(request.Url);
        Assert.Null(request.UrlTitle);
    }

    [Theory]
    [InlineData("http://homeblaze.local/devices")]
    [InlineData("HTTPS://example.com/a?b=c")]
    public void WhenUrlIsAnAbsoluteHttpOrHttpsAddress_ThenItIsSentWithItsUrlTitle(string url)
    {
        // Act
        var request = Map(new Notification { Message = "Hello", Url = url, UrlTitle = "Open" });

        // Assert
        Assert.Equal(url, request.Url);
        Assert.Equal("Open", request.UrlTitle);
    }

    [Fact]
    public void WhenUrlTitleIsTooLong_ThenItIsShortenedWhileTheUrlStaysIntact()
    {
        // Act
        var request = Map(new Notification { Message = "Hello", Url = "https://example.com", UrlTitle = new string('a', 300) });

        // Assert
        Assert.Equal("https://example.com", request.Url);
        Assert.Equal(PushoverMessageMapping.MaximumUrlTitleLength, request.UrlTitle!.Length);
    }
}
