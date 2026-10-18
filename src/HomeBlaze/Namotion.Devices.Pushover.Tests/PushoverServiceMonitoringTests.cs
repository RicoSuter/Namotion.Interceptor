using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Devices.Pushover.Client;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;
using Observer = System.Reactive.Observer;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverServiceMonitoringTests
{
    private const string ValidatePath = "/1/users/validate.json";
    private const string ReceiptPath = "/1/receipts/receipt1.json";
    private const string MessagesPath = "/1/messages.json";
    private const string NotAcknowledged = """{"status":1,"acknowledged":0,"expired":0}""";
    private const string Acknowledged = """{"status":1,"acknowledged":1,"acknowledged_at":1700000000,"acknowledged_by_device":"phone","expired":0}""";

    [Fact]
    public async Task WhenStarted_ThenTargetsAreValidatedAndShowTheirDevices()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.OK, """{"status":1,"devices":["phone"]}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        var target = service.Targets["rico"];
        Assert.Equal(ServiceStatus.Running, target.Status);
        Assert.Equal(["phone"], target.Devices);
        Assert.Null(target.StatusMessage);
    }

    [Fact]
    public async Task WhenUserKeyIsInvalid_ThenTargetStatusIsErrorAndServiceRuns()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.BadRequest, """{"user":"invalid","errors":["user key is invalid"],"status":0}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Targets["rico"].Status == ServiceStatus.Error);
        Assert.Equal("user key is invalid", service.Targets["rico"].StatusMessage);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
    }

    [Fact]
    public async Task WhenValidationFailsTransiently_ThenItRetriesWithoutMarkingTheTargetFailed()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var validationAttempts = 0;
        handler.Respond = request =>
        {
            if (request.Path != ValidatePath)
            {
                return (HttpStatusCode.OK, FakePushoverHandler.Success);
            }

            // The first attempt is a transient outage (503); the retry succeeds.
            return Interlocked.Increment(ref validationAttempts) == 1
                ? (HttpStatusCode.ServiceUnavailable, "Service Unavailable")
                : (HttpStatusCode.OK, """{"status":1,"devices":["phone"]}""");
        };

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Targets["rico"].Status == ServiceStatus.Running);
        var target = service.Targets["rico"];
        Assert.Equal("user-rico", target.UserKey);
        Assert.Null(target.StatusMessage);
        Assert.Equal(["phone"], target.Devices);
        Assert.True(validationAttempts >= 2);
    }

    [Fact]
    public async Task WhenApplicationTokenIsInvalid_ThenServiceStatusIsError()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.BadRequest, """{"token":"invalid","errors":["application token is invalid"],"status":0}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Error);
        Assert.Equal("The application token is invalid", service.StatusMessage);
    }

    [Fact]
    public async Task WhenNoApplicationTokenIsConfigured_ThenServiceIsStoppedWithoutRequests()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.ApplicationToken = null;

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.StatusMessage == "No application token configured");
        Assert.Equal(ServiceStatus.Stopped, service.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhenTheApplicationTokenIsConfiguredAfterBeingMissing_ThenTheServiceRecoversToRunning()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService("rico");
        service.ApplicationToken = null;

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.StatusMessage == "No application token configured");

        // Act
        service.ApplicationToken = "app-token";
        await service.Targets["rico"].ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
    }

    [Fact]
    public async Task WhenConfigurationIsApplied_ThenTargetsAreValidatedAgain()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 1);

        // Act
        await service.Targets["rico"].ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 2);
    }

    [Fact]
    public async Task WhenNoEmergencyIsActive_ThenNoReceiptRequestsAreMade()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        Assert.DoesNotContain(handler.Requests, request => request.Path.StartsWith("/1/receipts/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenEmergencyIsAcknowledged_ThenStateIsSetAndTheEventIsEmittedOnceWithTheResultId()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var receiptPolls = 0;
        handler.Respond = request => request.Path switch
        {
            MessagesPath => (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}"""),
            ReceiptPath => Interlocked.Increment(ref receiptPolls) < 2
                ? (HttpStatusCode.OK, NotAcknowledged)
                : (HttpStatusCode.OK, Acknowledged),
            _ => (HttpStatusCode.OK, FakePushoverHandler.Success)
        };

        var target = service.Targets["rico"];
        var events = new List<PushoverEmergencyAcknowledgedEvent>();
        using var subscription = target.Subscribe(CreateListObserver(events));

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

        // Act
        var result = await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert: wait on the event list (not a status property) so the emergency's other fields, written under the
        // tracker's lock before the event is raised, are already settled once this proceeds.
        await AsyncTestHelpers.WaitUntilAsync(() => CountOf(events) == 1);

        var emergency = target.Emergencies["receipt1"];
        Assert.Equal(PushoverEmergencyStatus.Acknowledged, emergency.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), emergency.AcknowledgedAt);
        Assert.Equal("phone", emergency.AcknowledgedByDevice);

        PushoverEmergencyAcknowledgedEvent acknowledged;
        lock (events)
        {
            acknowledged = Assert.Single(events);
        }

        Assert.Same(target, acknowledged.Target);
        Assert.Same(emergency, acknowledged.Emergency);
        Assert.Equal("receipt1", acknowledged.Id);
        Assert.Equal(result.Id, acknowledged.Id);
        Assert.Equal("phone", acknowledged.AcknowledgedByDevice);
    }

    [Fact]
    public async Task WhenTwoEmergenciesAreSentToOneTarget_ThenBothAreTrackedAndAcknowledgedIndependently()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var isSecondAcknowledged = false;
        handler.Respond = RespondWithNumberedReceipts(receipt => receipt switch
        {
            "receipt1" => Acknowledged,
            _ => Volatile.Read(ref isSecondAcknowledged) ? Acknowledged : NotAcknowledged
        });

        var target = service.Targets["rico"];
        var events = new List<PushoverEmergencyAcknowledgedEvent>();
        using var subscription = target.Subscribe(CreateListObserver(events));

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

        // Act
        var smoke = await target.SendNotificationAsync(new Notification { Message = "Smoke", Priority = NotificationPriority.Emergency }, CancellationToken.None);
        var water = await target.SendNotificationAsync(new Notification { Message = "Water", Priority = NotificationPriority.Emergency }, CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => CountOf(events) == 1);
        Assert.Equal(["receipt1", "receipt2"], target.Emergencies.Keys.Order());
        Assert.Equal(PushoverEmergencyStatus.Acknowledged, target.Emergencies[smoke.Id!].Status);
        Assert.Equal(PushoverEmergencyStatus.Active, target.Emergencies[water.Id!].Status);
        Assert.Equal("Water", target.Emergencies[water.Id!].Message);

        // Act
        Volatile.Write(ref isSecondAcknowledged, true);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => CountOf(events) == 2);
        Assert.Equal(PushoverEmergencyStatus.Acknowledged, target.Emergencies[water.Id!].Status);
        lock (events)
        {
            Assert.Equal([smoke.Id, water.Id], events.Select(acknowledged => acknowledged.Id));
        }
    }

    [Fact]
    public async Task WhenAnEventSubscriberThrows_ThenTheOtherSubscriberStillReceivesTheEventAndTheServiceStaysRunning()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = FakePushoverHandler.RespondWithReceipt(Acknowledged);

        var target = service.Targets["rico"];
        var events = new List<PushoverEmergencyAcknowledgedEvent>();

        // The throwing subscriber is registered first so a bare try/catch around the publish call alone would
        // not be enough: the fix must stop that subscriber's exception from also silencing the one after it.
        using var throwingSubscription = target.Subscribe(CreateThrowingObserver());
        using var subscription = target.Subscribe(CreateListObserver(events));

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => CountOf(events) == 1);
        Assert.Equal(ServiceStatus.Running, service.Status);
    }

    [Fact]
    public async Task WhenSignaledRightAfterAPoll_ThenTheReceiptIsNotPolledAgainWithinTheMinimumInterval()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.MinimumReceiptPollingInterval = TimeSpan.FromMilliseconds(300);
        var target = service.Targets["rico"];

        // The poll time the service recorded for each receipt request, read while that request is answered, so the
        // assertions do not depend on how quickly the test notices a request.
        var pollTimes = new List<DateTimeOffset>();
        var respondWithReceipt = FakePushoverHandler.RespondWithReceipt(NotAcknowledged);
        handler.Respond = request =>
        {
            if (request.Path == ReceiptPath)
            {
                lock (pollTimes)
                {
                    pollTimes.Add(target.Emergencies["receipt1"].LastPolledAt!.Value);
                }
            }

            return respondWithReceipt(request);
        };

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ReceiptPath).Length == 1);

        // Act: a configuration apply signals the loop immediately, well within the minimum poll interval.
        service.RequestValidation();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ReceiptPath).Length == 2);
        lock (pollTimes)
        {
            Assert.True(pollTimes[0] - target.Emergencies["receipt1"].SentAt >= service.MinimumReceiptPollingInterval);
            Assert.True(pollTimes[1] - pollTimes[0] >= service.MinimumReceiptPollingInterval);
        }
    }

    [Fact]
    public void WhenALateReceiptPollArrivesForAnEmergencyThatAlreadyEnded_ThenItIsIgnored()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService("rico");
        var target = service.Targets["rico"];
        var events = new List<PushoverEmergencyAcknowledgedEvent>();
        using var subscription = target.Subscribe(CreateListObserver(events));
        var now = DateTimeOffset.UtcNow;
        var added = service.EmergencyTracker.Add("rico", "receipt1", "Fire", "Kitchen", now, now + target.EmergencyExpire);
        service.EmergencyTracker.ApplyReceipt(added, new PushoverResponse { Status = 1, Expired = 1 });

        // Act
        service.EmergencyTracker.ApplyReceipt(added, new PushoverResponse
        {
            Status = 1,
            Acknowledged = 1,
            AcknowledgedAt = now.ToUnixTimeSeconds(),
            AcknowledgedByDevice = "phone"
        });

        // Assert
        var emergency = target.Emergencies["receipt1"];
        Assert.Equal(PushoverEmergencyStatus.Expired, emergency.Status);
        Assert.Null(emergency.AcknowledgedByDevice);
        Assert.Empty(events);
    }

    [Fact]
    public async Task WhenEmergencyExpires_ThenItsStatusIsExpired()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = FakePushoverHandler.RespondWithReceipt("""{"status":1,"acknowledged":0,"expired":1}""");
        var target = service.Targets["rico"];

        await using var running = await PushoverTestFactory.StartAsync(service);

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert
        var emergency = target.Emergencies["receipt1"];
        await AsyncTestHelpers.WaitUntilAsync(() => emergency.Status == PushoverEmergencyStatus.Expired);
        Assert.Null(emergency.AcknowledgedAt);
    }

    [Fact]
    public async Task WhenAReceiptCannotBeReadPastItsExpiry_ThenTheEmergencyIsGivenUpAsExpired()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = request => request.Path == ReceiptPath
            ? (HttpStatusCode.BadRequest, """{"status":0,"errors":["receipt not found"]}""")
            : (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}""");
        var target = service.Targets["rico"];
        var sentAt = DateTimeOffset.UtcNow - target.EmergencyExpire - TimeSpan.FromSeconds(1);
        service.EmergencyTracker.Add("rico", "receipt1", null, "Fire", sentAt, sentAt + target.EmergencyExpire);

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        var emergency = target.Emergencies["receipt1"];
        await AsyncTestHelpers.WaitUntilAsync(() => emergency.Status == PushoverEmergencyStatus.Expired);
    }

    [Fact]
    public async Task WhenOneOfTwoEmergenciesIsCanceled_ThenOnlyItIsCanceledAndTheOtherStaysActive()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = RespondWithNumberedReceipts(_ => NotAcknowledged);
        var target = service.Targets["rico"];
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        var first = target.Emergencies["receipt1"];

        // Act
        await first.CancelAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.Empty(handler.RequestsTo("/1/receipts/receipt2/cancel.json"));
        Assert.Equal(PushoverEmergencyStatus.Canceled, first.Status);
        Assert.Equal(PushoverEmergencyStatus.Active, target.Emergencies["receipt2"].Status);
    }

    [Fact]
    public async Task WhenAllEmergenciesOfATargetAreCanceled_ThenEachActiveOneIsCanceled()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = RespondWithNumberedReceipts(_ => NotAcknowledged);
        var target = service.Targets["rico"];
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Act
        await target.CancelAllEmergenciesAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.Single(handler.RequestsTo("/1/receipts/receipt2/cancel.json"));
        Assert.All(target.Emergencies.Values, emergency => Assert.Equal(PushoverEmergencyStatus.Canceled, emergency.Status));
    }

    [Fact]
    public async Task WhenCancelingOneOfSeveralEmergenciesFails_ThenTheOthersAreCanceledAndTheErrorNamesTheFailedOne()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var respondWithReceipts = RespondWithNumberedReceipts(_ => NotAcknowledged);
        handler.Respond = request => request.Path == "/1/receipts/receipt1/cancel.json"
            ? (HttpStatusCode.BadRequest, """{"status":0,"errors":["receipt not found"]}""")
            : respondWithReceipts(request);
        var target = service.Targets["rico"];
        await target.SendNotificationAsync(new Notification { Message = "Smoke", Title = "Smoke", Priority = NotificationPriority.Emergency }, CancellationToken.None);
        await target.SendNotificationAsync(new Notification { Message = "Water", Title = "Water", Priority = NotificationPriority.Emergency }, CancellationToken.None);

        // Act
        var exception = await Assert.ThrowsAsync<PushoverException>(() => target.CancelAllEmergenciesAsync(CancellationToken.None));

        // Assert
        Assert.Contains("1 of 2", exception.Message);
        Assert.Contains("Smoke", exception.Message);
        Assert.Contains("receipt not found", exception.Message);
        Assert.DoesNotContain("Water", exception.Message);
        Assert.Equal(PushoverEmergencyStatus.Active, target.Emergencies["receipt1"].Status);
        Assert.Equal(PushoverEmergencyStatus.Canceled, target.Emergencies["receipt2"].Status);
    }

    [Fact]
    public async Task WhenTheServiceCancelsAllEmergencies_ThenTheActiveEmergenciesOfEveryTargetAreCanceled()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico", "anna");
        handler.Respond = RespondWithNumberedReceipts(_ => NotAcknowledged);
        await service.Targets["rico"].SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await service.Targets["anna"].SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Act
        await service.CancelAllEmergenciesAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.Single(handler.RequestsTo("/1/receipts/receipt2/cancel.json"));
        Assert.Equal(PushoverEmergencyStatus.Canceled, service.Targets["rico"].Emergencies["receipt1"].Status);
        Assert.Equal(PushoverEmergencyStatus.Canceled, service.Targets["anna"].Emergencies["receipt2"].Status);
    }

    [Fact]
    public async Task WhenAFinishedEmergencyOutlivesTheRetention_ThenItIsRemovedAndTheActiveOneIsKept()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.FinishedEmergencyRetention = TimeSpan.FromMilliseconds(300);
        handler.Respond = RespondWithNumberedReceipts(receipt => receipt == "receipt1" ? Acknowledged : NotAcknowledged);
        var target = service.Targets["rico"];

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        var finished = target.Emergencies["receipt1"];
        await AsyncTestHelpers.WaitUntilAsync(() => finished.Status == PushoverEmergencyStatus.Acknowledged);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => !target.Emergencies.ContainsKey("receipt1"));
        Assert.True(DateTimeOffset.UtcNow >= finished.EndedAt + service.FinishedEmergencyRetention);
        Assert.Equal(PushoverEmergencyStatus.Active, target.Emergencies["receipt2"].Status);
    }

    private static int CountOf(List<PushoverEmergencyAcknowledgedEvent> events)
    {
        lock (events)
        {
            return events.Count;
        }
    }

    /// <summary>
    /// Answers each sent message with the next receipt ("receipt1", "receipt2", ...), each receipt poll with the body
    /// <paramref name="receiptBody"/> gives for that receipt, and every other request with success.
    /// </summary>
    private static Func<RecordedRequest, (HttpStatusCode StatusCode, string Body)> RespondWithNumberedReceipts(Func<string, string> receiptBody)
    {
        var receiptNumber = 0;
        return request =>
        {
            if (request.Path == MessagesPath)
            {
                return (HttpStatusCode.OK, $$"""{"status":1,"receipt":"receipt{{Interlocked.Increment(ref receiptNumber)}}"}""");
            }

            if (request.Path.StartsWith("/1/receipts/", StringComparison.Ordinal) && request.Path.EndsWith(".json", StringComparison.Ordinal) &&
                !request.Path.EndsWith("/cancel.json", StringComparison.Ordinal))
            {
                var receipt = request.Path["/1/receipts/".Length..^".json".Length];
                return (HttpStatusCode.OK, receiptBody(receipt));
            }

            return (HttpStatusCode.OK, FakePushoverHandler.Success);
        };
    }

    private static IObserver<PushoverEmergencyAcknowledgedEvent> CreateListObserver(List<PushoverEmergencyAcknowledgedEvent> events) =>
        Observer.Create<PushoverEmergencyAcknowledgedEvent>(
            value =>
            {
                lock (events)
                {
                    events.Add(value);
                }
            },
            _ => { },
            () => { });

    private static IObserver<PushoverEmergencyAcknowledgedEvent> CreateThrowingObserver() =>
        Observer.Create<PushoverEmergencyAcknowledgedEvent>(
            _ => throw new InvalidOperationException("Subscriber failure."),
            _ => { },
            () => { });
}
