using System.Diagnostics;
using System.Net;
using System.Reactive;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Devices.Pushover.Client;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverServiceMonitoringTests
{
    private const string ValidatePath = "/1/users/validate.json";
    private const string ReceiptPath = "/1/receipts/receipt1.json";

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
    public async Task WhenEmergencyIsAcknowledged_ThenStateIsSetAndTheEventIsEmittedOnce()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var receiptPolls = 0;
        handler.Respond = request => request.Path switch
        {
            "/1/messages.json" => (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}"""),
            ReceiptPath => Interlocked.Increment(ref receiptPolls) < 2
                ? (HttpStatusCode.OK, """{"status":1,"acknowledged":0,"expired":0}""")
                : (HttpStatusCode.OK, """{"status":1,"acknowledged":1,"acknowledged_at":1700000000,"acknowledged_by_device":"phone","expired":0}"""),
            _ => (HttpStatusCode.OK, FakePushoverHandler.Success)
        };

        var target = service.Targets["rico"];
        var events = new List<PushoverEmergencyAcknowledgedEvent>();
        using var subscription = target.Subscribe(CreateListObserver(events));

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert: wait on the event list (not a flag property) so the target's other emergency fields,
        // written under the same lock before the event is raised, are already settled once this proceeds.
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (events)
            {
                return events.Count == 1;
            }
        });

        Assert.True(target.IsEmergencyAcknowledged);
        Assert.False(target.IsEmergencyActive);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), target.EmergencyAcknowledgedAt);
        Assert.Equal("phone", target.EmergencyAcknowledgedByDevice);
        Assert.Null(target.EmergencyReceipt);

        PushoverEmergencyAcknowledgedEvent acknowledged;
        lock (events)
        {
            acknowledged = Assert.Single(events);
        }

        Assert.Same(target, acknowledged.Target);
        Assert.Equal("phone", acknowledged.AcknowledgedByDevice);
    }

    [Fact]
    public async Task WhenAnEventSubscriberThrows_ThenTheOtherSubscriberStillReceivesTheEventAndTheServiceStaysRunning()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = FakePushoverHandler.RespondWithReceipt(
            """{"status":1,"acknowledged":1,"acknowledged_at":1700000000,"acknowledged_by_device":"phone","expired":0}""");

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
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (events)
            {
                return events.Count == 1;
            }
        });
        Assert.Equal(ServiceStatus.Running, service.Status);
    }

    [Fact]
    public async Task WhenSignaledRightAfterAPoll_ThenTheReceiptIsNotPolledAgainWithinTheMinimumInterval()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.MinimumReceiptPollingInterval = TimeSpan.FromMilliseconds(300);
        handler.Respond = FakePushoverHandler.RespondWithReceipt("""{"status":1,"acknowledged":0,"expired":0}""");
        var target = service.Targets["rico"];

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ReceiptPath).Length == 1);
        var sinceFirstPoll = Stopwatch.StartNew();

        // Act: a configuration apply signals the loop immediately, well within the minimum poll interval.
        service.RequestValidation();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ReceiptPath).Length == 2);
        Assert.True(sinceFirstPoll.Elapsed >= service.MinimumReceiptPollingInterval);
    }

    [Fact]
    public void WhenALateReceiptPollArrivesForAReplacedReceipt_ThenItIsIgnored()
    {
        // Arrange
        var target = new PushoverTarget();
        var now = DateTimeOffset.UtcNow;
        target.StartEmergency("old-receipt", now);
        target.StartEmergency("new-receipt", now);

        // Act
        target.ApplyReceipt("old-receipt", new PushoverResponse
        {
            Status = 1,
            Acknowledged = 1,
            AcknowledgedAt = now.ToUnixTimeSeconds(),
            AcknowledgedByDevice = "phone"
        });

        // Assert
        Assert.True(target.IsEmergencyActive);
        Assert.False(target.IsEmergencyAcknowledged);
        Assert.Equal("new-receipt", target.EmergencyReceipt);
    }

    [Fact]
    public async Task WhenEmergencyExpires_ThenExpiredIsSet()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = FakePushoverHandler.RespondWithReceipt("""{"status":1,"acknowledged":0,"expired":1}""");
        var target = service.Targets["rico"];

        await using var running = await PushoverTestFactory.StartAsync(service);

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert: wait on the receipt getter (which takes the emergency lock) rather than the IsEmergencyExpired
        // flag alone, so the other fields written under that lock are already settled once this proceeds.
        await AsyncTestHelpers.WaitUntilAsync(() => target.EmergencyReceipt is null);

        Assert.True(target.IsEmergencyExpired);
        Assert.False(target.IsEmergencyActive);
        Assert.False(target.IsEmergencyAcknowledged);
    }

    [Fact]
    public async Task WhenEmergencyIsCanceled_ThenCancelIsPostedAndEmergencyIsInactive()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = request => request.Path == "/1/messages.json"
            ? (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}""")
            : (HttpStatusCode.OK, FakePushoverHandler.Success);
        var target = service.Targets["rico"];
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Act
        await target.CancelEmergencyAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.False(target.IsEmergencyActive);
        Assert.Null(target.EmergencyReceipt);
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
