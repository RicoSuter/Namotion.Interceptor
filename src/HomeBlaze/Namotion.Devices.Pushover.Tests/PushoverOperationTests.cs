using System.Net;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

/// <summary>
/// Reads the operations of the service and its targets as the UI and the MCP tools do: from the method properties
/// that HomeBlaze registers.
/// </summary>
public class PushoverOperationTests
{
    [Fact]
    public async Task WhenTargetIsRegistered_ThenItsSendNotificationOperationTakesANotificationAndReturnsTheResult()
    {
        // Arrange
        var (service, handler) = CreateRegisteredService("rico");
        var registered = service.Targets["rico"].TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetProperty("SendNotification")!.TryGetMethodMetadata()!;
        var result = await method.InvokeAsync([new Notification { Message = "From the UI" }], serviceProvider: null, CancellationToken.None);

        // Assert
        var parameter = Assert.Single(method.Parameters, parameter => parameter.RequiresInput);
        Assert.Equal(typeof(Notification), parameter.Type);
        Assert.Equal(typeof(NotificationResult), method.ResultType);
        Assert.False(method.RequiresConfirmation);
        Assert.Equal("From the UI", Assert.Single(handler.Requests).Form["message"]);
        Assert.Equal("test", Assert.IsType<NotificationResult>(result).Id);
    }

    [Fact]
    public void WhenServiceIsRegistered_ThenItOffersSendToAllButIsNoPublisher()
    {
        // Arrange
        var (service, _) = CreateRegisteredService("rico");

        // Act
        var registered = service.TryGetRegisteredSubject()!;

        // Assert
        Assert.NotNull(registered.TryGetProperty("SendToAllTargets")?.TryGetMethodMetadata());
        Assert.Null(registered.TryGetProperty("SendNotification"));
    }

    [Fact]
    public void WhenServiceIsRegistered_ThenItOffersCancelAllEmergencies()
    {
        // Arrange
        var (service, _) = CreateRegisteredService("rico");

        // Act
        var registered = service.TryGetRegisteredSubject()!;

        // Assert
        Assert.NotNull(registered.TryGetProperty("CancelAllEmergencies")?.TryGetMethodMetadata());
    }

    [Fact]
    public async Task WhenAnEmergencyIsActive_ThenCancelAllEmergenciesOfTheTargetIsEnabledUntilItEnds()
    {
        // Arrange
        var (service, handler) = CreateRegisteredService("rico");
        handler.Respond = request => request.Path == "/1/messages.json"
            ? (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}""")
            : (HttpStatusCode.OK, FakePushoverHandler.Success);
        var target = service.Targets["rico"];
        var cancelAllEmergencies = target.TryGetRegisteredSubject()!.TryGetProperty("CancelAllEmergencies")!;
        Assert.False(cancelAllEmergencies.IsMethodEnabled());

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert
        Assert.True(cancelAllEmergencies.IsMethodEnabled());

        // Act
        await target.CancelAllEmergenciesAsync(CancellationToken.None);

        // Assert
        Assert.False(cancelAllEmergencies.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenAnEmergencyIsCanceled_ThenItsCancelOperationIsDisabled()
    {
        // Arrange
        var (service, handler) = CreateRegisteredService("rico");
        handler.Respond = request => request.Path == "/1/messages.json"
            ? (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}""")
            : (HttpStatusCode.OK, FakePushoverHandler.Success);
        var target = service.Targets["rico"];
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        var emergency = target.Emergencies["receipt1"];
        var cancel = emergency.TryGetRegisteredSubject()!.TryGetProperty("Cancel")!;
        Assert.True(cancel.IsMethodEnabled());

        // Act
        await cancel.TryGetMethodMetadata()!.InvokeAsync([], serviceProvider: null, CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestsTo("/1/receipts/receipt1/cancel.json"));
        Assert.Equal(PushoverEmergencyStatus.Canceled, emergency.Status);
        Assert.False(cancel.IsMethodEnabled());
    }

    private static (PushoverService Service, FakePushoverHandler Handler) CreateRegisteredService(params string[] targetKeys)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        var (service, handler) = PushoverTestFactory.CreateService(targetKeys);
        ((IInterceptorSubject)service).Context.AddFallbackContext(context);
        return (service, handler);
    }
}
