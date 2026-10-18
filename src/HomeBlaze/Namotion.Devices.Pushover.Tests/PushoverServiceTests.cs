using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Devices.Pushover.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverServiceTests
{
    private const string MessagesPath = "/1/messages.json";

    [Fact]
    public void WhenCreated_ThenHasDefaults()
    {
        // Act
        var target = new PushoverTarget();

        // Assert
        Assert.Equal("Pushover target", target.Title);
        Assert.Equal(TimeSpan.FromSeconds(60), target.EmergencyRetry);
        Assert.Equal(TimeSpan.FromHours(1), target.EmergencyExpire);
        Assert.Equal(ServiceStatus.Stopped, target.Status);
        Assert.Empty(target.Devices);
        Assert.False(target.IsEmergencyActive);
    }

    [Fact]
    public void WhenServiceHasNoName_ThenTitleIsPushover()
    {
        // Act
        var (service, _) = PushoverTestFactory.CreateService();

        // Assert
        Assert.Equal("Pushover", service.Title);
        Assert.Equal(ServiceStatus.Stopped, service.Status);
    }

    [Fact]
    public async Task WhenTargetSendsNotification_ThenMessageUsesTheTargetUserKey()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var target = service.Targets["rico"];

        // Act
        await target.SendNotificationAsync(new Notification { Message = "Door open", Title = "Home" }, CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.RequestsTo(MessagesPath));
        Assert.Equal("app-token", request.Form["token"]);
        Assert.Equal("user-rico", request.Form["user"]);
        Assert.Equal("Door open", request.Form["message"]);
        Assert.Equal("Home", request.Form["title"]);
        Assert.NotNull(target.LastSentAt);
    }

    [Fact]
    public async Task WhenEmergencyIsSent_ThenRetryAndExpireAreSentAndEmergencyIsActive()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}""");
        var target = service.Targets["rico"];

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.RequestsTo(MessagesPath));
        Assert.Equal("2", request.Form["priority"]);
        Assert.Equal("60", request.Form["retry"]);
        Assert.Equal("3600", request.Form["expire"]);
        Assert.True(target.IsEmergencyActive);
        Assert.Equal("receipt1", target.EmergencyReceipt);
    }

    [Fact]
    public async Task WhenSendingToAllTargets_ThenEachTargetReceivesOneMessage()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico", "anna");

        // Act
        await service.SendToAllTargetsAsync(new Notification { Message = "Hello" }, CancellationToken.None);

        // Assert
        var users = handler.RequestsTo(MessagesPath).Select(request => request.Form["user"]).Order();
        Assert.Equal(["user-anna", "user-rico"], users);
    }

    [Fact]
    public async Task WhenOneTargetFails_ThenTheOthersAreSentAndTheErrorNamesTheFailedTarget()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico", "anna");
        handler.Respond = request => request.Form["user"] == "user-anna"
            ? (HttpStatusCode.BadRequest, """{"user":"invalid","errors":["user identifier is invalid"],"status":0}""")
            : (HttpStatusCode.OK, FakePushoverHandler.Success);

        // Act
        var exception = await Assert.ThrowsAsync<PushoverException>(
            () => service.SendToAllTargetsAsync(new Notification { Message = "Hello" }, CancellationToken.None));

        // Assert
        Assert.Equal(2, handler.RequestsTo(MessagesPath).Length);
        Assert.Contains("anna: user identifier is invalid", exception.Message);
        Assert.DoesNotContain("rico", exception.Message);
        Assert.NotNull(service.Targets["rico"].LastSentAt);
    }

    [Fact]
    public async Task WhenNoTargetsAreConfigured_ThenSendingToAllFails()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendToAllTargetsAsync(new Notification { Message = "Hello" }, CancellationToken.None));
    }

    [Fact]
    public async Task WhenApplicationTokenIsMissing_ThenSendFailsWithoutRequest()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.ApplicationToken = null;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.Targets["rico"].SendNotificationAsync(new Notification { Message = "Hello" }, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhenTargetIsNotPartOfAService_ThenSendFails()
    {
        // Arrange
        var target = new PushoverTarget { Name = "rico", UserKey = "user" };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.SendNotificationAsync(new Notification { Message = "Hello" }, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTargetsAreReplaced_ThenNewTargetsSendThroughTheService()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var anna = new PushoverTarget { Name = "Anna", UserKey = "user-anna" };

        // Act
        service.Targets = new Dictionary<string, PushoverTarget> { ["anna"] = anna };
        await anna.SendNotificationAsync(new Notification { Message = "Hello" }, CancellationToken.None);

        // Assert
        Assert.Equal("user-anna", Assert.Single(handler.Requests).Form["user"]);
    }

    [Fact]
    public void WhenEmergencyTimesAreOutOfRange_ThenTheyAreClamped()
    {
        // Arrange
        var target = new PushoverTarget();

        // Act
        target.EmergencyRetry = TimeSpan.FromSeconds(5);
        target.EmergencyExpire = TimeSpan.FromHours(10);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), target.EmergencyRetry);
        Assert.Equal(TimeSpan.FromHours(3), target.EmergencyExpire);
    }

    [Theory]
    [InlineData("Rico", new string[0], "rico")]
    [InlineData("  Living Room Tablet ", new string[0], "living-room-tablet")]
    [InlineData("Rico", new[] { "rico" }, "rico-2")]
    [InlineData("Rico", new[] { "rico", "rico-2" }, "rico-3")]
    [InlineData("!!!", new string[0], "target")]
    public void WhenCreatingTargetKey_ThenKeyIsReadableAndUnique(string name, string[] existingKeys, string expected)
    {
        // Act
        var key = PushoverService.CreateTargetKey(name, existingKeys);

        // Assert
        Assert.Equal(expected, key);
    }
}
