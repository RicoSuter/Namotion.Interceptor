using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Messaging;
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
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
            var target = service.Targets["rico"];
            Assert.Equal(ServiceStatus.Running, target.Status);
            Assert.Equal(["phone"], target.Devices);
            Assert.Null(target.StatusMessage);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenUserKeyIsInvalid_ThenTargetStatusIsErrorAndServiceRuns()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.BadRequest, """{"user":"invalid","errors":["user key is invalid"],"status":0}""");

        // Act
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => service.Targets["rico"].Status == ServiceStatus.Error);
            Assert.Equal("user key is invalid", service.Targets["rico"].StatusMessage);
            await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenApplicationTokenIsInvalid_ThenServiceStatusIsError()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.BadRequest, """{"token":"invalid","errors":["application token is invalid"],"status":0}""");

        // Act
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Error);
            Assert.Equal("The application token is invalid", service.StatusMessage);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenNoApplicationTokenIsConfigured_ThenServiceIsStoppedWithoutRequests()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.ApplicationToken = null;

        // Act
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => service.StatusMessage == "No application token configured");
            Assert.Equal(ServiceStatus.Stopped, service.Status);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenConfigurationIsApplied_ThenTargetsAreValidatedAgain()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        await service.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 1);

            // Act
            await service.Targets["rico"].ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
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
        using var subscription = target.Subscribe(new ListObserver(events));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);

            // Act
            await target.SendNotificationAsync(new Notification { Message = "Fire", Priority = NotificationPriority.Emergency }, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => target.IsEmergencyAcknowledged);
            Assert.False(target.IsEmergencyActive);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), target.EmergencyAcknowledgedAt);
            Assert.Equal("phone", target.EmergencyAcknowledgedByDevice);
            Assert.Null(target.EmergencyReceipt);

            var acknowledged = Assert.Single(events);
            Assert.Same(target, acknowledged.Target);
            Assert.Equal("phone", acknowledged.AcknowledgedByDevice);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenEmergencyExpires_ThenExpiredIsSet()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = request => request.Path switch
        {
            "/1/messages.json" => (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}"""),
            ReceiptPath => (HttpStatusCode.OK, """{"status":1,"acknowledged":0,"expired":1}"""),
            _ => (HttpStatusCode.OK, FakePushoverHandler.Success)
        };
        var target = service.Targets["rico"];

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await target.SendNotificationAsync(new Notification { Message = "Fire", Priority = NotificationPriority.Emergency }, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => target.IsEmergencyExpired);
            Assert.False(target.IsEmergencyActive);
            Assert.False(target.IsEmergencyAcknowledged);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
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
        await target.SendNotificationAsync(new Notification { Message = "Fire", Priority = NotificationPriority.Emergency }, CancellationToken.None);

        // Act
        await target.CancelEmergencyAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.False(target.IsEmergencyActive);
        Assert.Null(target.EmergencyReceipt);
    }

    private sealed class ListObserver(List<PushoverEmergencyAcknowledgedEvent> events) : IObserver<PushoverEmergencyAcknowledgedEvent>
    {
        public void OnNext(PushoverEmergencyAcknowledgedEvent value)
        {
            lock (events)
            {
                events.Add(value);
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
