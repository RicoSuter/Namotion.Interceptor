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
        Assert.Contains("safety-critical", method.Description);
        Assert.Equal("From the UI", Assert.Single(handler.Requests).Form["message"]);
        Assert.Equal("test", Assert.IsType<NotificationResult>(result).Id);
    }

    [Fact]
    public void WhenServiceIsRegistered_ThenItOffersSendToAllAndCancelAllEmergenciesButIsNoPublisher()
    {
        // Arrange
        var (service, _) = CreateRegisteredService("rico");

        // Act
        var registered = service.TryGetRegisteredSubject()!;

        // Assert
        Assert.NotNull(registered.TryGetProperty("SendToAllTargets")?.TryGetMethodMetadata());
        Assert.NotNull(registered.TryGetProperty("CancelAllEmergencies")?.TryGetMethodMetadata());
        Assert.Null(registered.TryGetProperty("SendNotification"));
    }

    [Fact]
    public async Task WhenAnEmergencyIsActive_ThenCancelAllEmergenciesOfTheTargetIsEnabledUntilItEnds()
    {
        // Arrange
        var (service, handler) = CreateRegisteredService("rico");
        handler.Respond = FakePushoverHandler.RespondWithNumberedReceipts();
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
        handler.Respond = FakePushoverHandler.RespondWithNumberedReceipts();
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

    [Fact]
    public async Task WhenAConfigurationReloadReplacesTheTargetInstance_ThenItsEmergencyIsAttachedAgainWithItsState()
    {
        // Arrange
        var lifecycleChanges = new RecordingLifecycleHandler();
        var (service, handler) = CreateRegisteredService(lifecycleChanges, "rico");
        handler.Respond = FakePushoverHandler.RespondWithNumberedReceipts();
        var oldTarget = service.Targets["rico"];
        await oldTarget.SendNotificationAsync(PushoverTestFactory.EmergencyNotification, CancellationToken.None);
        var emergency = oldTarget.Emergencies["receipt1"];
        var newTarget = new PushoverTarget { Name = "Rico", UserKey = "user-rico" };

        // Act
        service.Targets = new Dictionary<string, PushoverTarget> { ["rico"] = newTarget };

        // Assert
        Assert.Same(emergency, newTarget.Emergencies["receipt1"]);
        Assert.Empty(oldTarget.Emergencies);
        Assert.True(lifecycleChanges.IsAttached(emergency));
        Assert.NotNull(emergency.TryGetRegisteredSubject());
        Assert.Equal("receipt1", emergency.Receipt);
        Assert.Equal(PushoverEmergencyStatus.Active, emergency.Status);
        Assert.True(emergency.TryGetRegisteredSubject()!.TryGetProperty("Cancel")!.IsMethodEnabled());
    }

    private static (PushoverService Service, FakePushoverHandler Handler) CreateRegisteredService(params string[] targetKeys) =>
        CreateRegisteredService(lifecycleChanges: null, targetKeys);

    private static (PushoverService Service, FakePushoverHandler Handler) CreateRegisteredService(
        RecordingLifecycleHandler? lifecycleChanges, params string[] targetKeys)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        if (lifecycleChanges is not null)
        {
            context.WithService<ILifecycleHandler>(() => lifecycleChanges, handler => ReferenceEquals(handler, lifecycleChanges));
        }

        var (service, handler) = PushoverTestFactory.CreateService(targetKeys);
        ((IInterceptorSubject)service).Context.AddFallbackContext(context);
        return (service, handler);
    }

    /// <summary>
    /// Records whether each subject is in the graph, from the last context attach or detach it went through.
    /// </summary>
    private sealed class RecordingLifecycleHandler : ILifecycleHandler
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<IInterceptorSubject, bool> _isAttachedBySubject = new(ReferenceEqualityComparer.Instance);

        public bool IsAttached(IInterceptorSubject subject)
        {
            lock (_lock)
            {
                return _isAttachedBySubject.GetValueOrDefault(subject);
            }
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach || change.IsContextDetach)
            {
                lock (_lock)
                {
                    _isAttachedBySubject[change.Subject] = change.IsContextAttach;
                }
            }
        }
    }
}
