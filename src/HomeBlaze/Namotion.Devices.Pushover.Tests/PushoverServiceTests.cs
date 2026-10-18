using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor.Testing;
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
        Assert.Empty(target.Emergencies);
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
    public async Task WhenTargetSendsANormalNotification_ThenTheResultIdIsThePushoverRequestId()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.OK, """{"status":1,"request":"request1"}""");
        var target = service.Targets["rico"];

        // Act
        var result = await target.SendNotificationAsync(new Notification { Message = "Door open" }, CancellationToken.None);

        // Assert
        Assert.Equal("request1", result.Id);
    }

    [Fact]
    public async Task WhenEmergencyIsSent_ThenRetryAndExpireAreSentAndAnActiveEmergencyIsTrackedUnderItsReceipt()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.OK, """{"status":1,"request":"request1","receipt":"receipt1"}""");
        var target = service.Targets["rico"];
        var notification = new Notification { Message = "Smoke in the kitchen", Title = "Smoke", Priority = NotificationPriority.Emergency };

        // Act
        var result = await target.SendNotificationAsync(notification, CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.RequestsTo(MessagesPath));
        Assert.Equal("2", request.Form["priority"]);
        Assert.Equal("60", request.Form["retry"]);
        Assert.Equal("3600", request.Form["expire"]);
        Assert.Equal("receipt1", result.Id);

        var (receipt, emergency) = Assert.Single(target.Emergencies);
        Assert.Equal("receipt1", receipt);
        Assert.Equal("receipt1", emergency.Receipt);
        Assert.Equal("Smoke", emergency.Title);
        Assert.Equal("Smoke in the kitchen", emergency.Message);
        Assert.Equal(target.LastSentAt, emergency.SentAt);
        Assert.Equal(PushoverEmergencyStatus.Active, emergency.Status);
        Assert.Equal("rico", emergency.TargetKey);
    }

    [Fact]
    public void WhenAnEmergencyHasNoTitle_ThenItsDisplayTitleIsEmergency()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService("rico");
        var now = DateTimeOffset.UtcNow;

        // Act
        var emergency = service.EmergencyTracker.Add("rico", "receipt1", title: null, "Fire", now, now + TimeSpan.FromHours(1));

        // Assert
        Assert.Null(emergency.Title);
        Assert.Equal("Emergency", ((ITitleProvider)emergency).Title);
        Assert.Equal("Error", emergency.IconColor);
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
    public async Task WhenATargetInstanceIsReplacedForTheSameKey_ThenItsEmergenciesMoveToTheNewInstance()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = RespondWithNumberedReceipts();
        var oldTarget = service.Targets["rico"];
        await oldTarget.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await oldTarget.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        var emergencies = oldTarget.Emergencies;

        var newTarget = new PushoverTarget { Name = "Rico", UserKey = "user-rico" };

        // Act: a configuration reload replaces the target instance but keeps its "rico" key.
        service.Targets = new Dictionary<string, PushoverTarget> { ["rico"] = newTarget };

        // Assert
        Assert.Same(emergencies["receipt1"], newTarget.Emergencies["receipt1"]);
        Assert.Same(emergencies["receipt2"], newTarget.Emergencies["receipt2"]);
        Assert.Empty(oldTarget.Emergencies);

        // Act: the moved emergency can still be canceled through the new instance.
        await newTarget.Emergencies["receipt1"].CancelAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.Equal(PushoverEmergencyStatus.Canceled, newTarget.Emergencies["receipt1"].Status);
        Assert.Equal(PushoverEmergencyStatus.Active, newTarget.Emergencies["receipt2"].Status);
    }

    [Fact]
    public async Task WhenATargetInstanceIsReplacedWhileAnEmergencyIsBeingSent_ThenTheNewInstanceTracksPollsAndCancelsIt()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = FakePushoverHandler.RespondWithReceipt("""{"status":1,"acknowledged":0,"expired":0}""");
        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeRespond = () => release.Task;
        var oldTarget = service.Targets["rico"];
        var sending = oldTarget.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(MessagesPath).Length == 1);

        var newTarget = new PushoverTarget { Name = "Rico", UserKey = "user-rico" };

        // Act: a configuration reload replaces the target instance while the send is still waiting for Pushover.
        service.Targets = new Dictionary<string, PushoverTarget> { ["rico"] = newTarget };
        release.SetResult();
        var result = await sending;

        // Assert
        var emergency = newTarget.Emergencies[result.Id!];
        Assert.Empty(oldTarget.Emergencies);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo("/1/receipts/receipt1.json").Length >= 1);

        // Act
        await emergency.CancelAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.Equal(PushoverEmergencyStatus.Canceled, emergency.Status);
    }

    [Fact]
    public async Task WhenATargetIsRemovedWhileAnEmergencyIsBeingSent_ThenTheEmergencyIsCanceled()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = FakePushoverHandler.RespondWithReceipt("""{"status":1,"acknowledged":0,"expired":0}""");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeRespond = () => release.Task;
        var target = service.Targets["rico"];
        var sending = target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(MessagesPath).Length == 1);

        // Act
        service.Targets = new Dictionary<string, PushoverTarget>();
        release.SetResult();
        var result = await sending;

        // Assert
        Assert.Equal("receipt1", result.Id);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo("/1/receipts/receipt1/cancel.json").Length == 1);
        Assert.Empty(target.Emergencies);
    }

    [Fact]
    public async Task WhenATargetWithActiveEmergenciesIsRemoved_ThenTheyAreCanceledInTheBackgroundAndItsServiceReferenceIsCleared()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = RespondWithNumberedReceipts();
        var target = service.Targets["rico"];
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Act
        service.Targets = new Dictionary<string, PushoverTarget>();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            handler.RequestsTo("/1/receipts/receipt1/cancel.json").Length == 1 &&
            handler.RequestsTo("/1/receipts/receipt2/cancel.json").Length == 1);
        Assert.Null(target.Service);
    }

    [Fact]
    public async Task WhenCancelingAnEmergencyOfARemovedTargetFails_ThenAWarningIsLoggedAndTheOthersAreStillCanceled()
    {
        // Arrange
        var handler = new FakePushoverHandler();
        var logger = new RecordingLogger<PushoverService>();
        var service = new PushoverService(new TestHttpClientFactory(handler), logger) { ApplicationToken = "app-token" };
        var target = new PushoverTarget { Name = "Rico", UserKey = "user-rico" };
        service.Targets = new Dictionary<string, PushoverTarget> { ["rico"] = target };

        var respondWithReceipts = RespondWithNumberedReceipts();
        handler.Respond = request => request.Path == "/1/receipts/receipt1/cancel.json"
            ? (HttpStatusCode.BadRequest, """{"status":0,"errors":["receipt not found"]}""")
            : respondWithReceipts(request);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        var second = target.Emergencies["receipt2"];

        // Act
        service.Targets = new Dictionary<string, PushoverTarget>();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => second.Status == PushoverEmergencyStatus.Canceled);
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Warnings.Count == 1);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("removed Pushover targets", warning.Message);
        Assert.Contains("1 of 2", warning.Exception!.Message);
        Assert.Contains("receipt not found", warning.Exception.Message);
    }

    [Fact]
    public void WhenATargetWithoutActiveEmergenciesIsRemoved_ThenNoCancelRequestIsMadeAndItsServiceReferenceIsCleared()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var target = service.Targets["rico"];

        // Act
        service.Targets = new Dictionary<string, PushoverTarget>();

        // Assert
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("cancel", StringComparison.Ordinal));
        Assert.Null(target.Service);
    }

    [Fact]
    public async Task WhenNotificationPriorityIsUndefined_ThenSendingFailsWithoutARequest()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var target = service.Targets["rico"];
        var notification = new Notification { Message = "Hello", Priority = (NotificationPriority)7 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => target.SendNotificationAsync(notification, CancellationToken.None));
        Assert.Empty(handler.Requests);
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

    private static Func<RecordedRequest, (HttpStatusCode StatusCode, string Body)> RespondWithNumberedReceipts()
    {
        var receiptNumber = 0;
        return request => request.Path == MessagesPath
            ? (HttpStatusCode.OK, $$"""{"status":1,"receipt":"receipt{{Interlocked.Increment(ref receiptNumber)}}"}""")
            : (HttpStatusCode.OK, FakePushoverHandler.Success);
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
