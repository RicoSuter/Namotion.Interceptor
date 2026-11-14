using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

/// <summary>
/// Reads the enabled state of the operations of a charger as the property panel and the page widgets do: from the
/// attribute of the property that the operation is registered as.
/// </summary>
public class WallboxChargerOperationEnabledTests
{
    [Fact]
    public void WhenChargerIsDisconnected_ThenItsLockAndChargingOperationsAreDisabled()
    {
        // Arrange
        var charger = CreateRegisteredCharger();
        string[] operations = ["Lock", "Unlock", "PauseCharging", "ResumeCharging"];

        // Act
        charger.IsConnected = false;

        // Assert
        var registered = charger.TryGetRegisteredSubject()!;
        Assert.All(operations, operation => Assert.False(registered.TryGetProperty(operation)!.IsMethodEnabled(), operation));
    }

    [Fact]
    public void WhenConnectedAndUnlocked_ThenLockIsEnabledAndUnlockIsDisabled()
    {
        // Arrange
        var charger = CreateRegisteredCharger();

        // Act
        charger.IsConnected = true;
        charger.IsLocked = false;

        // Assert
        var registered = charger.TryGetRegisteredSubject()!;
        Assert.True(registered.TryGetProperty("Lock")!.IsMethodEnabled());
        Assert.False(registered.TryGetProperty("Unlock")!.IsMethodEnabled());
    }

    private static WallboxCharger CreateRegisteredCharger()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        var charger = new WallboxCharger(new TestHttpClientFactory(), NullLogger<WallboxCharger>.Instance);
        ((IInterceptorSubject)charger).Context.AddFallbackContext(context);
        return charger;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
