using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Pushover.Client;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverMessageMappingTests
{
    private static Dictionary<string, string> Map(Notification notification) =>
        PushoverMessageMapping.Create("app", "user", notification, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), "rico", NullLogger.Instance);

    [Theory]
    [InlineData(NotificationPriority.Low, "-1")]
    [InlineData(NotificationPriority.Normal, null)]
    [InlineData(NotificationPriority.High, "1")]
    [InlineData(NotificationPriority.Emergency, "2")]
    public void WhenMappingPriority_ThenThePushoverLevelIsSent(NotificationPriority priority, string? expected)
    {
        // Act
        var fields = Map(new Notification { Message = "Hello", Priority = priority });

        // Assert
        Assert.Equal(expected, fields.GetValueOrDefault("priority"));
    }

    [Fact]
    public void WhenNotificationIsNormal_ThenPriorityRetryExpireAndTagsAreOmitted()
    {
        // Act
        var fields = Map(new Notification { Message = "Hello", Title = "Title", Url = "https://example.com", UrlTitle = "Open" });

        // Assert
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["token"] = "app",
                ["user"] = "user",
                ["message"] = "Hello",
                ["title"] = "Title",
                ["url"] = "https://example.com",
                ["url_title"] = "Open"
            },
            fields);
    }

    [Theory]
    [InlineData("rico", "target-rico")]
    [InlineData("Living Room, 2", "target-living-room--2")]
    public void WhenCreatingTheEmergencyTag_ThenEveryCharacterOtherThanALetterDigitOrDashIsADash(string targetKey, string expected)
    {
        // Act
        var tag = PushoverMessageMapping.GetEmergencyTag(targetKey);

        // Assert
        Assert.Equal(expected, tag);
    }

    [Fact]
    public void WhenMessageIsTooLong_ThenItIsShortenedWithAnEllipsis()
    {
        // Act
        var fields = Map(new Notification { Message = new string('a', 2000), Title = new string('b', 300) });

        // Assert
        Assert.Equal(PushoverMessageMapping.MaximumMessageLength, fields["message"].Length);
        Assert.EndsWith("…", fields["message"]);
        Assert.Equal(PushoverMessageMapping.MaximumTitleLength, fields["title"].Length);
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

    public static TheoryData<string> OmittedUrls() => new()
    {
        "ftp://example.com/file",
        "javascript:alert(1)",
        "/Devices/Kitchen",
        "example.com",
        "not a url",
        "https://example.com/" + new string('a', 600)
    };

    [Theory]
    [MemberData(nameof(OmittedUrls))]
    public void WhenUrlIsNotAnAbsoluteHttpOrHttpsAddressOrTooLong_ThenItAndItsUrlTitleAreOmitted(string url)
    {
        // Act
        var fields = Map(new Notification { Message = "Hello", Url = url, UrlTitle = "Open" });

        // Assert
        Assert.False(fields.ContainsKey("url"));
        Assert.False(fields.ContainsKey("url_title"));
    }

    [Theory]
    [InlineData("http://homeblaze.local/devices")]
    [InlineData("HTTPS://example.com/a?b=c")]
    public void WhenUrlIsAnAbsoluteHttpOrHttpsAddress_ThenItIsSentWithItsUrlTitle(string url)
    {
        // Act
        var fields = Map(new Notification { Message = "Hello", Url = url, UrlTitle = "Open" });

        // Assert
        Assert.Equal(url, fields["url"]);
        Assert.Equal("Open", fields["url_title"]);
    }

    [Fact]
    public void WhenUrlTitleIsTooLong_ThenItIsShortenedWhileTheUrlStaysIntact()
    {
        // Act
        var fields = Map(new Notification { Message = "Hello", Url = "https://example.com", UrlTitle = new string('a', 300) });

        // Assert
        Assert.Equal("https://example.com", fields["url"]);
        Assert.Equal(PushoverMessageMapping.MaximumUrlTitleLength, fields["url_title"].Length);
    }
}
