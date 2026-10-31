using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Services.Pushover.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace Namotion.Services.Pushover.Tests;

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
        Assert.Contains("safety-critical", method.Description);
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
        Assert.False(registered.TryGetProperty("SendToAllTargets")!.TryGetMethodMetadata()!.RequiresConfirmation);
        Assert.Null(registered.TryGetProperty("SendNotification"));
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
        ((IInterceptorSubject)service).AttachToContext(context);
        return (service, handler);
    }
}
