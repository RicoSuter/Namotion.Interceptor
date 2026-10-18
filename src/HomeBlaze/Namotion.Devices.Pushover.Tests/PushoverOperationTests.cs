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
    public async Task WhenTargetIsRegistered_ThenItsSendNotificationOperationTakesANotification()
    {
        // Arrange
        var (service, handler) = CreateRegisteredService("rico");
        var registered = service.Targets["rico"].TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetProperty("SendNotification")!.TryGetMethodMetadata()!;
        await method.InvokeAsync([new Notification { Message = "From the UI" }], serviceProvider: null, CancellationToken.None);

        // Assert
        var parameter = Assert.Single(method.Parameters, parameter => parameter.RequiresInput);
        Assert.Equal(typeof(Notification), parameter.Type);
        Assert.False(method.RequiresConfirmation);
        Assert.Equal("From the UI", Assert.Single(handler.Requests).Form["message"]);
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
    public async Task WhenAnEmergencyIsActive_ThenCancelEmergencyIsEnabled()
    {
        // Arrange
        var (service, handler) = CreateRegisteredService("rico");
        handler.Respond = _ => (HttpStatusCode.OK, """{"status":1,"receipt":"receipt1"}""");
        var target = service.Targets["rico"];
        var cancelEmergency = target.TryGetRegisteredSubject()!.TryGetProperty("CancelEmergency")!;
        Assert.False(cancelEmergency.IsMethodEnabled());

        // Act
        await target.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);

        // Assert
        Assert.True(cancelEmergency.IsMethodEnabled());
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
